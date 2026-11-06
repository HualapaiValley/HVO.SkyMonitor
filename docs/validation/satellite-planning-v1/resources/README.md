# Warm local planning resource observations

The real `CameraAgentSatellitePlanner` was compared with direct calls to the same
orbital engine at source `0c48a01974df2a8d2bb1e73f9af56e7c6e4e5485`.
Both modes execute the same one-hour pass query and every returned original-query
arc: one ISS pass at Seattle, or two Starlink passes at HVO. Existing retained
snapshots, actual frozen EOP/leaps, observer settings and nominal reduced rigs are
used unchanged. The celestial catalog is empty. Snapshot/time adapters are warm
and memory-only; source parsing is outside the timed boundary.

The direct engine is the baseline. The facade additionally performs admission,
repeated authoritative context/source verification, canonical hashing and
original-session retention. It makes four memory snapshot-reader calls for ISS
and six for the two Starlinks; direct mode makes none. This comparison measures
the added behavior, not a claim that the facade does identical work for free.
No HTTP, authentication, Blazor rendering, cold disk I/O, catalog parsing or
capture/publication is timed.

## Protocol and results

The protocol was recorded before any measurement. After candidate gates and the
focused test replay stopped, four fresh processes ran in the predeclared order
core → facade → facade → core. Each process performed two warmups followed by
five measured calls per case, with `DOTNET_TieredCompilation=0`. All 40 measured
calls completed; no samples or outliers were discarded. SDK10.0.402,
.NET runtime10.0.13, Linux x86-64, eight reported processors, workstation GC.
The original manifest records UTC times, exact commands and binary hashes.

| Case / mode | Elapsed median (min–max), ms | CPU median, ms | Allocated median, decimal MB | Working set after, min–max MiB |
| --- | ---: | ---: | ---: | ---: |
| ISS / direct | 39.36 (33.50–42.26) | 47.55 | 31.69 | 80.53–81.86 |
| ISS / facade | 37.38 (30.44–47.57) | 44.20 | 33.48 | 81.89–86.93 |
| Two Starlinks / direct | 55.50 (48.51–65.37) | 64.80 | 61.23 | 80.29–82.34 |
| Two Starlinks / facade | 73.15 (50.18–129.93) | 82.67 | 63.81 | 81.62–85.79 |

Allocations increase by about1.79MB for ISS and2.58MB for the two-Starlink
operation. That boundary includes the extra source/context checks, retained
results and serialization; these observations do not isolate the cost of each
stage. The two facade Starlink process medians were86.60ms and57.75ms, so the
pooled73.15ms is not evidence of a stable32% slowdown. The faster ISS facade
median likewise does not establish an improvement. Preserve the variation and
interpret these small samples as an observation, not statistical equivalence.

Process CPU includes asynchronous work; process-wide allocation counters include
background runtime allocation. Working sets are observed process values, and
peak RSS is a lifetime process maximum, not a per-query retained-memory measure.
The process metadata sampling itself is outside the query stopwatch. There is
no queue/throughput or live full-population result here. The existing busy and
session-cap tests cover admission/retention semantics separately. These samples
neither validate a full-population source reader nor qualify #1395's five-second
frame-available-to-local-publication window.

Every measured mode/case retained the same combined canonical product hash,
pass count, arc sample count and actual propagation evaluation count:

| Case | Passes | Arc samples | Actual propagation evaluations | Combined canonical SHA-256 |
| --- | ---: | ---: | ---: | --- |
| ISS | 1 | 663 | 6310 | `3CA2C21A7F5F8D75DE4DAFAE45B2BC220F87509A0714E2CF0ECC23595684B73A` |
| Two Starlinks | 2 | 1288 | 12102 | `70BAC78194BB1AD353A83BEEF4EDA712D61400DBFA9E4DD5DFAF9CD278FE7C0D` |

The combined hash is SHA-256 of UTF-8 pipe-separated pass/arc canonical product
hashes, ordered by parent pass hash; it is not the byte hash of a JSON container.
This is a consumer preservation check, not an independent numerical oracle.

## Files and reproduction

[summary.json](summary.json) contains all aggregates and identity checks.
`measurement-*.json` retain every raw sample, exact input/model/context identities
and limitations. [predeclared-plan.json](predeclared-plan.json) preserves the
original protocol and premeasurement build/metadata corrections.
[run-manifest.json](run-manifest.json) records the actual local commands and
hashes; its absolute paths are provenance from that run, not installation paths.
[checksums.json](checksums.json) binds this portable pack.

The source is retained as `Program.cs.txt` plus `Harness.csproj.template`, rather
than adding an executable project to the solution. It includes the existing
`PlanningTestInputs.cs` helper from the repository and references production
Common; there is no alternate propagator or new package dependency. The helper's
unused stub callbacks are explicitly set to their existing null defaults during
untimed setup. `reproduce.py` materializes a temporary project outside the
checkout, builds with the pinned SDK and records four fresh process runs:

```bash
python3 docs/validation/satellite-planning-v1/resources/reproduce.py \
  "$PWD" /absolute/path/to/new-external-resource-run
```

Run only on an otherwise idle host, with no test/build workers. Retain all
outputs. The script checks the frozen canonical identities and actual work
counts without imposing a post-hoc timing threshold. Compare its recorded source
and binary hashes to the original manifest; later code/SDK/host changes are a
new experiment, not a replay of the original environment. No element/time data
are downloaded. No benchmark result approves runtime source installation,
production enablement, physical accuracy or capture cadence.
