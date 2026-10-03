# Shared astrometry validation ledger

Issue: #1088. Base: development/v1 at9f5dbb837f8bf7180bd0f33d757d8f26b7e8bdd6. Tier C because this is shared full-frame image processing and a measured solver path.

## Baseline and combined POC gate

- Existing Astronomy Unit baseline184/184 passed at the base
- Preserved standalone blind/warm POC:194 assertions,28 original groups,52 exposure/color groups
- Independent combined-PoC audit used all35 saved integrated frames across seven20s/60s mono/RGGB cases, including the170degree placeholder;35 cold and28 warm results passed, with32 additional rejection/metadata controls
- Combined audit maximum rotation error0.005646degree, relative focal error0.005661%, verification RMS0.1312px, association truth precision1.0; generic scaled fixtures only
- Wrong UTC+10minutes still fits at about99/100 with about2.504degrees orientation shift, so metadata trust is independent

## Portable new repository tests

- Astronomy contract/geometry tests: blind/warm recovery, compact evidence identity/round trips, RA/Dec/local scale, stale/observer mismatch, insufficient sources, incomplete/unsupported catalogs, cancellation/time budget, malformed inputs, deterministic ordering and timestamp degeneracy
- Adversarial tests: nearby narrow-field aliases, off-center unequal-focal perspective, wrong parity/model, injected false sources and radial-model boundary trials
- Pixel pipeline tests: deterministic time-integrated floating Gaussian deposition with one read-noise realization;20s mono,60s mono,60s linear reconstructed RGGB and170degree RGGB. These fixture renders omit photon noise; the independent preserved POC matrix includes it
- Every pipeline fit comes from measured pixels. Truth enters only generation/evaluation. Three-frame spherical registration uses accepted shared solver mappings and shows narrower ideal moment PSFs than unregistered averaging; zero-contributor/ground samples stay invalid
- Imaging tests cover isolated/hot/blended/rectangular/masked/noisy centroids, limits/cancellation/nonfinite inputs, four CFA phases, affine/constant-channel reconstruction and scientific precision

Reproduce focused tests with SDK10.0.401:

```
dotnet build tests/HVO.SkyMonitor.Astronomy.Tests/HVO.SkyMonitor.Astronomy.Tests.csproj -c Release -m:1 /p:UseSharedCompilation=false -warnaserror
dotnet test tests/HVO.SkyMonitor.Astronomy.Tests/HVO.SkyMonitor.Astronomy.Tests.csproj -c Release --no-build --filter 'TestCategory=Unit'
dotnet test tests/HVO.SkyMonitor.Imaging.Tests/HVO.SkyMonitor.Imaging.Tests.csproj -c Release --filter 'TestCategory=Unit' -m:1 /p:UseSharedCompilation=false
```

The single-process MSBuild switches avoid this executor's named-pipe restriction; they do not weaken test selection. Project-based formatting may require a less restricted executor; folder-mode whitespace validation is distinct from the complete formatter gate.

## Resource workload

`AstrometricPerformanceTests.FullFrame_DetectionColdWarmResourcesAreMeasured` is Manual, with W1-sized1936×1216 mono and W2-sized3096×2080 generic RGGB-derived linear luminance. The calibrated geometry is explicitly synthetic/provisional, not physical-camera qualification. Each case renders once outside timing, uses five warm-ups and30 measured synchronous detection/refinement operations, concurrency1, no measured I/O or queue. Cold is measured separately. JSON reports allocations, retained image bytes, process peak working set, CPU, median/p95 latency and throughput. Repeated immutable-frame processing measures the kernel, not live capture cadence.

```
dotnet test tests/HVO.SkyMonitor.Astronomy.Tests/HVO.SkyMonitor.Astronomy.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~AstrometricPerformanceTests' --logger trx --results-directory artifacts/astrometry-resources
```

There was no previous production solver to compare. Cold and warm paths are compared on identical inputs; the preserved POC's smaller640/768px timings are not presented as an equivalent full-resolution baseline.

## Required candidate gates

The issue requires classifier-selected evidence and independent exact-range review. All local results and any blocked gates must be recorded against the final candidate. Docker CLI/socket are absent in this cloud executor, so Docker-dependent integration gates cannot be declared passed. This limitation prevents a full Tier C merge-ready claim. No publication/PR/merge/deployment or application registration is part of the current local checkpoint.

## PR1093 review corrections

- Warm absolute focal bounds now survive local refinement and successive accepted updates; the relative2% trust interval is intersected with the immutable-calibration bounds and projection domain. Fixed-scale, upper/lower drift and out-of-range-prior regressions cover this contract
- Catalog completeness now declares a finite magnitude limit and binds both that limit and incomplete/truncated status into selection provenance. Sync, async-provider and warm paths reject insufficient requested coverage; matching-ceiling controls still solve
- Nine focused regression cases were added. The repository Astronomy inventory is216 Unit cases; full solution inventory is4631 Unit cases. These are category inventories, not claims that environment-blocked host tests passed

## Bounded installed-catalog qualification (#1101)

`AstrometricCatalogSourceTests` checks fixture/direct-load refusal, exact
magnitude selection, entry bounds, deterministic identity, negative/invalid
magnitudes and cancellation. Installed DI reuses the same catalog cache for the
astrometry contract. Shared solver regression tests preserve standalone
identity and reject incompatible package provenance for warm refinement.

The explicit full-snapshot gate requires an offline, resolver-validated HYG 4.2
installation. Missing evidence fails; the test never falls back to fixture data
or downloads a catalog. Build/install a private test bundle using the existing
[catalog runbook](../catalog/production-install.md), then run:

```bash
DOTNET_TieredCompilation=0 \
HVO_ASTROMETRY_CATALOG_ROOT=/absolute/private/test/catalog-root \
HVO_EVIDENCE_REVISION="$(git rev-parse HEAD)" \
dotnet test tests/HVO.SkyMonitor.Catalog.Sqlite.Tests/HVO.SkyMonitor.Catalog.Sqlite.Tests.csproj \
  --configuration Release --filter "FullyQualifiedName~FullSnapshotSelectionResources" \
  --logger trx --results-directory TestResults/astrometric-catalog
```

This manual gate checks the approved database hash and independent known counts
(1,637 at magnitude 5; 2,865 at 5.5; 15,598 at 7), exact-cap/one-over-cap behavior,
package provenance and solver refusal of an overflowing production selection.
It compares the existing full candidate query followed by honest bounded
materialization with the new bounded adapter: concurrency 1, 5 warmups and 30
measured operations for each of those three magnitude ceilings, maximum 2,500
rows. Run without another build/benchmark and disable tiered compilation for
both routes to avoid comparing different JIT warm-up phases.

The JSON/TRX attachment records every sample, selection hash, exact supplied
revision, catalog identity, startup allocation/time, median/p95 wall time,
process CPU, allocations and process working-set high water. Warm reads perform
no disk/network I/O and have no queue/backlog. Process high water is not
operation-local peak memory. The issue's measured-host acceptance is p95 <=20 ms
and allocations <= the equivalent baseline plus 32 KiB; median regression above
max(20%, 0.5 ms) requires investigation and a recorded explanation. Latency
budgets describe this qualification host, not a portable hardware guarantee.
Final immutable-head results and environment are retained in the issue/PR
ledger. Full camera/solve/detection qualification remains #1102/#1106.

The [actual virtual-camera baseline](virtual-camera-astrometry-baseline.md)
defines #1102's ordinary final-pixel harness, independent Cartesian references,
supported readouts, held-out partitions, timing limitation and resource gates.
It supplements the standalone fixtures above; final exposure-correct evidence
remains dependent on #522/#1106.
