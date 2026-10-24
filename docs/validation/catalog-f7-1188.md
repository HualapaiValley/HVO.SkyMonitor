# Deferred F7 catalog qualification (#1188)

This record owns the full catalog-only comparison deferred from #1186. The
foundation and curated annotation delivery do not establish this qualification.
No production optimization, catalog trust change, release, deployment, hardware
qualification or scientific acceptance is included.

## Qualification state

**VALID 0.** The current campaign has not run. This preparation record must not
be interpreted as a performance verdict or a supported operating envelope.

The original execution11 remains incomplete: ten source trials had quiet checks
and individual validators return zero, but the finite work deadline stopped the
24-trial comparison at 61 passed gates and 58 not run. A separate late receipt
used 90,112 allocated bytes against its 65,536-byte allowance. The original
nonzero exits, failure records and incomplete comparison remain unchanged.
The foundation's 416 functional passes are separate evidence.

## Pinned comparison

The source baseline is `764233766a2d42da823241954469f48b086a7e06`.
The current catalog candidate is
`f57dfc1b2ddf0a646f1455a15b4e4eaac4e23c29` on `development/v1`.
The two Foundation measurement files and `issue-525-query-pins.json` are
byte-identical to historical candidate
`b60fa1d430f8f8183d95caae0dceaa6ccc8af236`:

| Input | SHA-256 |
| --- | --- |
| `CatalogPerformanceTests.Foundation.cs` | `8a9620d89f5f91deb8774bdee6f3d46f9166d6943a5bd2a79ba1a494a44c5802` |
| `CatalogPerformanceTests.FoundationDso.cs` | `a9c1a29784ae7e87b184d3aed70a1adeb8d3ad35fa1eb426a94cd71acf31c0be` |
| `issue-525-query-pins.json` | `acb6f7600850307aac42a5b5fe021885f4b26c4507f1f4dfed6f333e806c30f6` |

Both arms use the repository's pinned .NET SDK 10.0.402. The baseline needs a
declared SDK configuration overlay in addition to the historical test-only
overlay; its catalog implementation stays at the original baseline. Source,
test-overlay, dependency, runtime and binary identities must be recorded
separately. The source comparison includes the actual package versions at each
revision and does not isolate a causal effect of the foundation alone.

The immutable catalog inputs remain:

| Arm | Package | Database SHA-256 |
| --- | --- | --- |
| Stars | `hyg-v4.4-p4-s3-r1` | `2cc06890de1d4168f1eb91ca4ef5cde1c4923f275a52a1aedfec002df42a3321` |
| Composed | `hyg-v4.4-openngc-v20260501-p5-s4-r1` | `eea1181ffae1dca2935aeed2c7790305b28045061f38ca6dda5eaf19694a8f1f` |

The source comparison runs baseline stars against candidate stars; the
composition comparison runs candidate stars against candidate composed. Each
comparison uses three consecutive ABBA blocks, twelve fresh processes, and six
adjacent A/B pairs. All 24 trials run in one serial session. Query measurements
retain five warmups and 100 operations, the 50-pointing region sweep, and all 15
nonempty pinned deep-sky queries. The linear deep-sky baselines run on the same
candidate; no deep-sky operation is invented for a stars-only catalog.

## Unchanged acceptance

The existing rules require explanation for startup exceeding both twice its
paired baseline and an additional 100 ms; common star query median or p95
exceeding both 10% and an additional 1 ms; and any deep-sky query p95 exceeding
its equivalent linear baseline p95 by more than 1 ms. Unexplained material
regression blocks qualification. Incomplete prefixes yield no regression verdict.

Startup includes package authentication, validation, eager row loading and
immutable index construction. CPU, allocations, working set, storage reads,
logical reads and read calls stay distinct. Query verification remains outside
the measured operation. CPU-counter zeros mean below resolution. Startup has
six values per arm and reports median and range, without a startup p95 claim.

The original strict no-steal and runqueue predicates, 2-GiB process peak limit,
8-GiB cohort allowance, component and reserve limits, 119 ordered gates and
finite phase bounds remain. Only this host's shared heavy gate may be reserved.
Boundary samples do not prove continuous quietness or continuous peak
containment. Fresh processes do not imply cold operating-system storage caches.

## Recovered harness and failures

The complete reviewed v6 control source was recovered with its original
81,173-byte length and SHA-256
`2aeed76da244d8ce59dbacebdcf87e17705c93c55793c4e5ab14cdadee061ed5`.
Its complete independent report was recovered with SHA-256
`6d3901bfcf00ba75ddf38d4d6fb823f7c60eb0713885aba5e5abe6231e75cd66`.
The recorded requested reviewer was Astra/xhigh; actual historical model and
effort remain unknown. This recovery supplies no current PR convergence credit.

Both reported blockers were reproduced before correction:

- **F7-V6-IR-1:** 122 extracted files produce at least 610 mandatory filesystem
  events and a 91,086-byte trace. The actual complete v6 run rejects both its
  first normal evidence and failed evidence at the 65,536-byte record limit.
- **F7-V6-IR-2:** the setup-error oracle returns `matched: true` for an empty
  preservation wait object. The counterexample fixture follows the caught
  setup-error path; it does not claim a real child wait.

The recovered v7 draft uses complete ordered trace chunks and typed terminal
agreement. Its first actual execution published fifteen completed cases, then
stopped on `NAMED_PHASE` in `v5-fallback-EIO-existing-partial-no-zero`.
The failure and complete failed trace were retained. Subsequent fail-first
verification found inspection faults occurring before their named partial write,
incorrect errno exception classes, aggregate raw traces exceeding 8 MiB, and
failure progress referring to a previous case. Each red remains preserved.

The [current private controls](../../scripts/catalog/f7/README.md) correct those
cases without changing the selected executor logic or scientific predicates.
All 90 controls and 119 fake gate interactions pass. Complete snapshots contain
10,334,069 reconstructed bytes; deterministic lossless compression keeps the
complete native observer output at 852,193 logical bytes and 1,740,800 allocated
bytes, within the unchanged output limits. The actual adapter exits zero with
raw wait status zero in 1.61 seconds, sends no signals, and observes an empty
released cohort. Typed terminal, multi-chunk reconstruction, corruption and
failure-preservation regressions pass. This establishes the private control
model and its observed adapter, without a native performance or crash-survival
claim.

Both 37-file campaign source sets match their recorded manifests. The complete
original 621/894-entry packets and some dated serialized inventory bindings
remain unavailable. Current 66/90 source cases and all 128 distinct historical
case IDs were recovered; derived inventories do not recreate missing original
hashes. New packet seals and original historical seals must stay distinct.

## Evidence and next gate

The owning issue's append-only ledger records the recovered sources, exact
per-file provenance, fail-first receipts and subsequent control runs. Retain all
failed attempts. Before any valid comparison, require completed current controls,
complete bounded output reconstruction, actual terminal/resource/release
receipts, a conservative fitting-window assessment and fresh source/package
bindings. The eventual results must state their measured envelope and limits.

The merge profile is `development-v1`: Preflight plus Build and Unit after
independent review. Tier C local candidate gates and the separate F7 acceptance
remain required. A development integration pass does not establish production
qualification.
