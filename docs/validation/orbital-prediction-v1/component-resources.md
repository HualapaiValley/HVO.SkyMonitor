# Bounded component resource baseline (#529)

2026-10-19, base `795ada39cac505725abb4ec63b7d68ed7c9c189f` in isolated T3
worktree `t3-a7f4575e`, provider `codex` (primary/account1), model
`gpt-6.1-sol`, runtime-reported reasoning `xhigh`. This is an absolute baseline
of the existing public engine, with first/repeat comparisons. No production
algorithm changed. It complements the [numerical qualification](README.md).

The workload requests every retained object, but **does not complete the
population**. All six population calls return `Partial/EvaluationBudget`.
All twelve ordinary ISS/GEO calls complete. This is component engineering
evidence; supported adoption, physical accuracy, optical visibility, raster
cost, full-host contention and frame-available-to-publication cadence remain
outside this record, including #1395.

## Reproduce offline

`satellite-resources` takes a new output directory, existing camera settings,
canonical manifest/payload, already-local EOP/leap files, explicit UTC start,
and the two canonical rig settings. It performs no acquisition, GP mapping,
network resolution, DI registration or per-query input I/O. The research
mapper that prepared this snapshot is **not #528 production normalized data**.
There was no provider refresh for this work.

```bash
dotnet build tools/HVO.SkyMonitor.VirtualSkyPoc/HVO.SkyMonitor.VirtualSkyPoc.csproj \
  --no-restore --configuration Release -warnaserror

evidence_root=/home/roys/.codex/artifacts/batch-a-529-2026-10-19
timeout 60s dotnet tools/HVO.SkyMonitor.VirtualSkyPoc/bin/Release/net10.0/HVO.SkyMonitor.VirtualSkyPoc.dll \
  satellite-resources "$evidence_root/component-resources-child/reproduction-v1/results" \
  "$evidence_root/virtual-harness-child/settings-seattle.json" \
  "$evidence_root/qualification-prep/canonical-inputs-v1/active-population/manifest.json" \
  "$evidence_root/qualification-prep/canonical-inputs-v1/active-population/records.json" \
  tests/fixtures/orbital-prediction-v1/time/predicted-2026.finals2000A \
  tests/fixtures/orbital-prediction-v1/time/leap-seconds.list \
  2026-10-20T13:03:32.287674Z \
  src/HVO.SkyMonitor.CameraAgent/virtual-asi174.full.json \
  src/HVO.SkyMonitor.CameraAgent/virtual-asi178mc.full.json
```

Choose a new versioned output path for another run; an existing directory is
rejected. The frozen engineering run is retained externally under
`/home/roys/.codex/artifacts/batch-a-529-2026-10-19/component-resources-child/run-v2/`:

- `command.sh`, `run.log`, `machine-and-source.json`, `source.diff` and the
  command's retained source identify the launch and measured code.
- `results/workload.json` is written before the queries. `preparation.json`,
  `measurements.json`, `selection-scan.json`, `comparisons.json` and
  `environment.json` contain machine-readable resources, counter definitions,
  statuses, input hashes, geometry/model identities and acceptance results.
- Each query retains its full result and each product's canonical bytes,
  canonical-file SHA-256 and contract `ProductSha256`. `checksums.json` binds
  the retained files. Retained provider bytes are not published in this repository.

Two engineering processes were launched, within the limit of three. `run-v1`
retains an initial harness failure: duplicate anonymous-object JSON names
prevented writing the first measurement. Removing the duplicate field fixed
the harness; the request, engine and budgets were unchanged for `run-v2`.
The failed run is not pooled with the completed evidence. No additional
performance suite, host matrix or unchanged repeated run was executed.

## Frozen workload and measurement

The retained population contains 16,754 objects/records and 22,248,813 payload
bytes. Logical manifest identity is
`D63A161FBD3550D89D62CF300EE0A4FDA761DA31B952F529481CDCD876809BAA`;
payload identity is
`59D21C1A67D972F341C2AD6B43A5E9894E6C677DDAA2066C70543060A5966C19`.
The canonical manifest **file** SHA-256 is
`52E52E6CF6C51E9EA7F923BDDFC94C372F4D4B52087774753CC7D5BD6C3B7B1D`,
which differs from its logical manifest identity by design. Its retained raw
source lineage is
`DFEE7C9E5D9D58E1889200A4610F278D3E9809A8A3FA0A5E392040D52C96B62A`.
Completeness applies only to that retained provider active-group response.

All windows begin at `2026-10-20T13:03:32.287674Z`. Observer is Seattle,
47.6062 degrees north, -122.3321 degrees east, 50 m ellipsoidal height assumed.
The source policy uses rank zero for each retained source and withholds stale
diagnostics. WGS72/AFSPC-a, predicted local EOP, pinned leap seconds and the
existing geometric, unrefracted conversion/projection conventions are used.

Population requests have 16,754 requested slots, a 5 s window, 150,000 core
evaluation reservations, 256 tracks, 20,000 emitted samples, 1,500 ms elapsed
budget, maximum sample step 1 s and chord tolerance 0.25 px. Single-object
requests use the same work limits except one object/track and a maximum 100 s
window. These limits deliberately bound CPU, allocation and products while
exercising a useful population prefix; they are not a claim that 1,500 ms or
150,000 evaluations suffices for every object. The original request was frozen
before the first run and was not enlarged after a limit was reached.

There are nine scenarios, each with two immediate sequential repetitions:
supplied-geometry population; ISS (`25544`) at 5/20/100 s; GOES 18 (`51850`,
synchronous GEO example) at 5/20/100 s; W1 population; W2 population. Concurrency
is one, warm-up count zero, arrivals zero, initial backlog zero, fault/outage
duration zero. There is no ingestion queue; drain rate/age is N/A. No forced
GC or per-frame file lookup occurs. Private evaluator caches and initializers
are recreated per object/query; the snapshot index and resolved time data are
reused. Repetition does not reuse propagated point results across queries.

The supplied rig is 640 x 640 upward equidistant fisheye, circular radius
313.6 px. W1 uses the exact ASI174 1936 x 1216 Mono16 rig, principal point
(968,608), radius 595.84 px. W2 uses the exact ASI178 3096 x 2080 RGGB16 rig,
principal point (1548,1040), radius 1187.5 px and calibrated focal length
735.553926 px. W1/W2 retain the Seattle observer. All use configured geometry,
geometric horizon, identity full-sensor readout and the remaining image
transform exactly once. Rig/readout/calibration hashes are in `workload.json`.
No 4,708,352-byte W1 or 12,879,360-byte W2 raster is created: these are numerical
projection geometries, not the canonical 5-warm-up/30-measurement full-frame
algorithm benchmark in [performance-validation](../../planning/performance-validation.md).

`Stopwatch` surrounds the complete `PredictExposureAsync` method boundary,
including public query validation, contract validation, defensive result
freezing and result construction. Preparation, request construction, reporting
and canonical product serialization are outside that boundary. The result's
`ElapsedMilliseconds` is retained separately as a **prefinal snapshot**; it is
not substituted for absolute method latency. Process CPU and
`GC.GetTotalAllocatedBytes(precise: true)` deltas cover the operation; GC counts
are recorded per generation. Working set is sampled at boundaries and peak
working set is the process-lifetime OS high-water mark. These are not isolated
per-operation memory peaks or retained managed-heap measurements.

Environment: SDK 10.0.402, .NET 10.0.13, Release, native Linux x64 process on
Ubuntu 24.04.4 LTS; workstation GC/Interactive, monotonic timer frequency
1 GHz. KVM exposes eight cores of an Intel Core Ultra 9 285H and 33,654,861,824
bytes RAM. Storage is a QEMU virtual disk with ext4; physical media is not
observable. No containers or services are involved. Other owner work may run
on the host; this is not an isolated host qualification. Tiered JIT, background
runtime CPU, GC and first/repeat order remain confounded. CPU can exceed wall
time. No p95, steady-state or statistically significant speedup is claimed.

## Preparation and lookup contribution

All values below come from completed `run-v2`; allocations are bytes. Cold
means first use in a new process, not a flushed OS page cache.

| Stage | Wall ms | CPU ms | Allocated bytes | Boundary working set after, bytes |
| --- | ---: | ---: | ---: | ---: |
| Bounded reads of all seven local inputs | 10.728 | 11.716 | 22,302,264 | 85,221,376 |
| Validated canonical parse/materialization/object index | 2734.826 | 3090.999 | 2,208,018,168 | 497,537,024 |
| Resolve local EOP/leaps | 3.450 | 3.462 | 45,672 | 497,647,616 |
| Deserialize three rig settings | 79.020 | 79.038 | 2,044,216 | 500,273,152 |
| Construct policy/model/engine/three geometries | 28.359 | 28.394 | 957,400 | 501,002,240 |
| Construct nine immutable queries | 16.905 | 16.928 | 2,871,464 | 503,582,720 |
| Post-query indexed lookup of all 16,754 objects | 3.374 | 3.392 | 136 | 593,465,344 |
| Post-query full static selection/domain scan | 14.737 | 14.750 | 26,488,432 | 593,498,112 |

The 22,269,808 logical input bytes are read once before queries. Reporting and
product writes are excluded from query time, but affect later process working
set. No filesystem syscall trace was collected; database/object-store/network
operations are absent from the engine path. The full scan is deliberately
after the queries so it does not pre-warm selection before the first query.

Parsing alone incurs 217 Gen0, seven Gen1 and three Gen2 collections. This
confirms the existing #1402 preparation concern; it is outside frame processing
but is substantial startup cost, not eliminated by calling the input immutable.
The index-lookup scan is inexpensive and does not reparse or rescan source
bytes. Initialization/cache time cannot be isolated through this public API.
Source inspection shows two charged core calls for each successful initializer
and a query-local UTC-tick point cache; the public evaluation total includes
initializer calls. Cache hits/initializer counts are explicitly unknown in
the machine record. No reflection or new production instrumentation was added.

## Population resource contribution

`first/repeat` denotes call order, not independent trials. All rows stop on
`EvaluationBudget`, at exactly 150,000 reservations, below the 1,500 ms elapsed,
256-track and 20,000-sample limits. Decimal MB below means 1,000,000 bytes.

| Geometry/call | Method ms | CPU ms | Alloc MB | Engine prefinal ms | Working set after / lifetime peak MB | Evaluations/s |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Supplied first | 639.216 | 707.758 | 692.455 | 585.075 | 515.842 | 234,662 |
| Supplied repeat | 571.865 | 770.382 | 691.576 | 521.882 | 526.299 | 262,300 |
| W1 first | 636.190 | 789.913 | 691.652 | 581.699 | 555.225 | 235,779 |
| W1 repeat | 409.702 | 610.762 | 690.885 | 362.224 | 566.600 | 366,119 |
| W2 first | 321.032 | 459.970 | 682.811 | 278.910 | 576.025 | 467,243 |
| W2 repeat | 307.800 | 309.784 | 682.791 | 266.313 | 585.003 | 487,330 |

Result validation/freezing costs an additional 41.487–54.491 ms after the
engine's snapshot. The first/repeat supplied wall-time change is -67.351 ms
(-10.5%), while allocation changes by only -0.13%. The repeat CPU is higher.
This is a comparison, not evidence of an algorithmic improvement or permission
to extrapolate completion time to the full population. Later W2 is also a
different clipping geometry and runs after more JIT activity.

The full static selection scan finds 15,949 selected, 805 policy-withheld
(776 stale, 29 outside the policy window), 186 numerical-profile rejections
among selected and 15,763 statically supported selected objects. Those are
**static population facts**, not actual propagation counts.

| Actual bounded query, same counts in both repetitions | Supplied | W1 | W2 |
| --- | ---: | ---: | ---: |
| Requested | 16,754 | 16,754 | 16,754 |
| Visited (`EvaluatedObjects`) | 1,969 | 1,969 | 1,965 |
| Unvisited | 14,785 | 14,785 | 14,789 |
| Selected among visited | 1,815 | 1,815 | 1,812 |
| Policy-withheld among visited | 154 | 154 | 153 |
| Static numerical-domain rejection reported by actual query | 14 | 14 | 14 |
| Geometrically excluded after successful processing | 1,601 | 1,601 | 1,609 |
| Returned objects with emitted projected pixels | 199 | 199 | 189 |
| Returned tracks | 199 | 199 | 189 |
| Complete / partial tracks | 199 / 0 | 199 / 0 | 188 / 1 |
| Emitted samples / samples with pixels | 2,246 / 2,217 | 2,246 / 2,217 | 2,159 / 2,106 |
| Point-propagated object count | Unknown | Unknown | Unknown |
| Proven lower bound for point-propagated objects | 1,800 | 1,800 | 1,798 |

Each query reports one additional evaluation-budget failure. `EvaluatedObjects`
counts object-loop visits, including selection failures, and is not the count
of objects actually point-propagated. Selected among visited subtracts reported
selection/missing-object failures. Numeric-domain counts use the exact
`outside-numerical-support-profile-v1` failure detail. Successful geometric
exclusions are the visited prefix minus distinct failure and product identities:
the inspected engine's only successful no-product path is a resolved geometric
exclusion. Returned/failed identities may overlap for a useful partial product.
Products plus successful exclusions establish a propagation lower bound, not an
exact count; initializer-only and failed-point work is not separately observable.
Projected objects count only returned objects with non-null emitted pixels;
internal projection attempts are unknown. The static domain mirror performs
no initialization, EOP-span validation, propagation or geometric exclusion.

The original run left exact initialization, point and internal projection object
counts open. The actual-operation rerun below now records them directly. Its
returned prefix still does not complete the population; unvisited objects stay
unknown, and integrated cadence remains separate.

## Ordinary one-object completion

The snapshot remains the full 16,754-object materialized snapshot even for a
single requested identity. All calls return `Complete/None`, one selected,
point-propagated, projected and returned object, no withholding/domain failure/
geometric exclusion. The public counter itself still does not expose a direct
point-propagated count; here the returned-product lower bound and one requested
object agree. Samples all contain pixels. Each first/repeat product matches
canonical bytes exactly.

| Object/window | Evaluations; samples | First/repeat method ms | First/repeat CPU ms | First/repeat allocated bytes | First/repeat working set after / lifetime peak MB |
| --- | ---: | ---: | ---: | ---: | ---: |
| ISS 5 s | 83; 11 | 4.126 / 3.263 | 4.144 / 3.277 | 1,538,992 / 1,534,744 | 534.659 / 535.425 |
| ISS 20 s | 323; 41 | 3.950 / 4.415 | 3.964 / 4.428 | 3,102,808 / 3,106,792 | 536.203 / 536.977 |
| ISS 100 s | 1,603; 201 | 12.622 / 12.511 | 12.646 / 12.529 | 11,864,424 / 11,730,192 | 538.464 / 540.586 |
| GOES 18 5 s | 83; 11 | 3.217 / 2.819 | 3.231 / 2.834 | 1,536,632 / 1,540,088 | 542.081 / 542.872 |
| GOES 18 20 s | 323; 41 | 3.891 / 4.411 | 3.896 / 4.424 | 3,108,904 / 3,108,904 | 543.666 / 544.465 |
| GOES 18 100 s | 1,603; 201 | 12.928 / 13.155 | 12.979 / 13.172 | 11,760,320 / 11,760,320 | 545.919 / 548.303 |

These are 76.0–354.7 complete queries/s for these single-object requests;
the raw record includes evaluation/visited-object throughput per call. The
method includes roughly 2.1–4.8 ms beyond the prefinal snapshot. Source
inspection shows the result constructor builds a record-hash dictionary over
the whole retained snapshot even for one object. This run does not isolate that
cost from product revalidation/hashing, so no per-stage optimization benefit is
claimed. Large preparation/query allocations and finalization cost are recorded
for the owner; this delegate changes no core/public contracts.

## Verification and handoff

Completed `run-v2` has zero acceptance failures: all twelve ordinary calls
complete with one track, and all 592 common complete products across the nine
pairs have identical canonical-file bytes. W2 also retains a useful partial
track in both calls. Partial/time-driven prefixes are not required to match.
No full-population completion, statistical tail latency, memory plateau or
cadence acceptance follows from this short process.

The focused tool Release build passes with zero warnings/errors. Full
`dotnet format` verification for the two owned C# files passes. Only these
files and this document are committed; existing `satellite-predict`,
`satellite-tracks` and `satellite-reference` dispatch is preserved. The offline
command's 18-query/byte-identity checks provide focused executable validation;
no broad test suite was run by this delegate.

The component range is classified using `scripts/ci:classify pull_request`
against `795ada39`, with exact heads/output retained externally in
`component-resources-child/classification-*.txt`. It returns `mode=full`,
`complete=true`, `deployment=false`, all five component lanes true, and
`secret_scan_contract=false` / `release_scripts=false` for three paths:
the tool lies outside the classifier's component boundaries. This selector
record is handed to the root; the explicitly bounded delegate scope does not
run the selected broad matrix. The root owns integration,
the full issue candidate gate, independent review and lifecycle. Its target is
`development/v1`: protected checks are `Development v1 / Preflight` and
`Development v1 / Build and Unit`; a full-matrix classifier output does not
authorize full qualification CI. This delegate performs no GitHub mutation,
push, PR, source refresh or merge.


## Actual-operation accounting rerun

Source `494ad721ff79784e59a30bb5caadad9e9dc32243`, 2026-10-20, adds immutable
query-owned [WorkDiagnostics](../../astronomy/orbital-work-accounting-v1.md).
The original nine scenarios, two repetitions, retained input hashes, geometries,
UTCs and bounds are unchanged. Only the measured source and reporting differ;
no network fetch occurred. The updated command writes workload schema v2.
Legacy manually constructed results retain null diagnostics, meaning unknown.

All 18 queries pass their original status/output gates: 12 ordinary calls are
Complete; six population calls remain Partial/EvaluationBudget at 150,000
reservations. All **1,186 canonical track files** match the original bytes,
including partial tracks. Actual query counters, rather than products or static
mirrors, give the following values in both repetitions:

| Actual operation facts | Supplied | W1 | W2 |
| --- | ---: | ---: | ---: |
| Requested / visited objects | 16,754 / 1,969 | 16,754 / 1,969 | 16,754 / 1,965 |
| Selected / static-support rejection | 1,815 / 14 | 1,815 / 14 | 1,812 / 14 |
| Initialization-attempted objects / core calls | 1,801 / 3,602 | 1,801 / 3,602 | 1,798 / 3,596 |
| Point-attempted objects / core calls | 1,801 / 146,398 | 1,801 / 146,398 | 1,798 / 146,404 |
| Successful / failed point calls | 146,398 / 0 | 146,398 / 0 | 146,404 / 0 |
| Projection-attempted objects | 199 | 199 | 199 |
| Projector calls / native-coordinate successes | 19,446 / 19,424 | 19,446 / 19,424 | 20,722 / 19,705 |
| Returned tracks with pixels | 199 | 199 | 189 |
| Unvisited objects | 14,785 | 14,785 | 14,789 |

Initialization means the pinned initializer operation (including its internal
epoch propagation) and separate epoch validation call, two reservations on
success. Point success means a valid TEME state before coordinate conversion.
Projector attempts include bounded, unbounded and chart calls; success means
native coordinates, even if later cropped or excluded. W2 therefore attempts
projection for ten objects that emit no track. Every row satisfies initialization
plus point calls equals the existing propagation reservation count. Cache reuse
adds no core attempts; cache-hit totals are still not collected. These facts
close the exact-count evidence gap for this **bounded workload**, not population
search completeness or physical visibility.

| Default-runtime population pair | Method first / repeat, ms | CPU first / repeat, ms | Allocations first / repeat, bytes |
| --- | ---: | ---: | ---: |
| Supplied | 713.95 / 710.99 | 1,042.74 / 1,020.28 | 692,470,096 / 691,567,968 |
| W1 | 353.44 / 352.56 | 501.38 / 435.19 | 689,910,224 / 689,895,144 |
| W2 | 344.31 / 352.52 | 361.92 / 369.40 | 682,806,016 / 682,807,376 |

The maximum process peak working set is 585,715,712 bytes versus the original
585,003,008. Snapshot preparation remains outside query timing. The first
population pair uses more CPU than the historical run, W1 uses less, and W2
varies. Default-run allocation changes range from -0.274% to +0.00244% across
all 18 observations. Do not pool first/repeat observations into an improvement
or isolated cold/warm claim.

To investigate that timing variation, one additional **ordered reference/current
pair** ran the same workload with `DOTNET_TieredCompilation=0` equally for both
binaries, no extra warm-up query, no forced GC and no concurrent test/build.
The retained reference binary and current binary are hash-bound in its declaration.
This is a diagnostic control, kept separate from default qualification. Repeat
population results after each same-path first call were:

| Controlled repeat | Reference / current method, ms | Reference / current CPU, ms | CPU change | Allocation increase |
| --- | ---: | ---: | ---: | ---: |
| Supplied | 470.82 / 477.13 | 470.80 / 477.22 | +1.36% | 12,176 bytes |
| W1 | 454.78 / 476.58 | 454.97 / 476.59 | +4.75% | 14,600 bytes |
| W2 | 457.53 / 467.27 | 457.50 / 467.25 | +2.13% | 12,152 bytes |

The control exposes a small recurring accounting cost: roughly 6–22ms more CPU
on these repeat population calls and less than 0.003% additional allocation.
The larger default initial-pair difference is consistent with tiered-JIT/order
confounding; this single pair cannot prove its precise cause or statistical size.
The current default queries stay below 714ms of their unchanged 1,500ms bound.
All 1,186 control canonical files also agree. No zero-overhead, performance
improvement, full-frame or integrated five-second claim is made.

[Machine-readable summary](work-accounting-resource-summary.json) retains the
before/after rows, actual counts, working sets and controlled observations.
External durable evidence under `batch-a-529-2026-10-19/work-accounting-resources/`
contains `run-v1/command.sh`, clean source/binary hashes, all results, comparison
and checksum manifests, and `controlled-pair-v1/` with its predeclared two-process
limit, exact environment, binary hashes, commands and outcomes. The historical
run is preserved. Reproduce the original command above with a fresh versioned
output directory using this source; diagnostics require no extra switch.
