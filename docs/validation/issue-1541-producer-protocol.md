# #1541 actual measured-association producer protocol

Declared before measurement on 2026-10-23. This covers the remaining local
acceptance of [#1187](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1187)
through [#1541](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1541).
Review, target synchronization and protected CI belong to the PR owner.

The foundation is already merged by #1540. Its frozen W2 comparison has the
producer **off**; it cannot qualify the producer. The original PR1190 findings
and the distinct PR1549 follow-ups remain in the results record. No default
profile activation, wider #526 accuracy/adverse scenarios, covariance
calibration, physical camera, native/ARM qualification or main promotion is
claimed.

## Workload and limits

Use `MeasuredStellarAssociationAcceptanceTests` in the CameraAgent test project,
with the explicit exhaustive qualification selection described below. Its
algorithms, numerical/resource ceilings and measurement windows stay unchanged.
Its ordinary composition captures final noisy VirtualSky pixels,
commits raw ingress, stages and publishes the projected scene, runs the standard
lane and actual opt-in measured-association node, then reloads the durable raw,
scene and product. Catalog `hyg-v42-production` must contain 119,625 rows with
database SHA256
`B51D18B722199E89AA8FE4622EBE507346C75EFFB375E546881452A263F0B9E2`;
the magnitude-5 query returns exactly 1,637 stars. Retain the catalog manifest.

- Cost: `RecipeStageCost`, Mono16 1936×1216 and RGGB16 3096×2080, UTC
  2026-01-15T08:00:00Z, seed 110201, equidistant projection, default variant,
  one-second exposure, `stellarCandidateSelection=Exhaustive`, magnitude 5,
  query bound 2000. Five warmup and thirty measured operations per arm per
  profile, alternating arm order each ordinal on the **same final pixels**.
  Arm A is the bare qualified measurer; B is the complete production recipe via
  `CameraAgentRecipeExecutionAdapter`. They perform different work; report
  B−A as overhead and B as the absolute producer baseline.
- Compare baseline production commit `6ea815f24e1522fe1ba81107fd9a3a2ad993e18e`
  with an identical test-only acceptance-harness overlay and a clean committed
  candidate using **five fresh-process interleaved pairs**, baseline
  first for odd pairs, candidate first for even pairs. Compare **B against B**.
  Record both source and binary revisions, assembly hashes and dirty state.
- Final-pixel accuracy: `ActualCaptureAccuracyAndReconstruction`, exactly
  21 cases: mono-native, mono-roi, mono-bin2, mono-roi-bin2, mono-mirror,
  mono-roll and cfa-native across UTCs January/May/September 15, 2026 at 08:00Z,
  seeds 110201/110205/110209. Independent Cartesian reference equations supply
  truth, never the production scene/projector. Each case requires zero wrong
  label-eligible catalog IDs, a nonempty finite eligible centroid subset and
  RMS ≤0.25 px. Report recall; no additional recall threshold is declared.
  Exactly three recipe replays must match durable payload bytes, content,
  output and recipe identities, checksum and ordered lineage.
- Concurrency 1, no scheduled arrivals or outages, zero initial backlog, fresh
  durable root per process. The single capture's standard lane must complete;
  no sustained-capacity or backlog-recovery claim follows. Upload is disabled
  here; real ingest/runner/SQL acceptance uses the separate integration suites.
- Retain capture, publish and claim/handle/complete timing; stage wall/CPU,
  process-wide allocation, GC, procfs I/O (including probe reads), product
  bytes, throughput and process-lifetime peak RSS. Peak RSS includes capture,
  composition and both recipe arms, and is not a recipe-only peak.

For each profile, the comparative wall/CPU sample unit is the median of thirty
B operations in one process. Compare the medians of five such process medians.
An increase is material above `max(10% of baseline median, baseline max−min)`.
For allocation use the same aggregation with `max(5%, 32 KiB/operation)`.
For RSS use the median of five process-lifetime peaks and the rule
`candidate − baseline > max(5% of baseline, 32 MiB)`. Report every trial,
minimum/median/maximum and paired deltas; never substitute favorable maxima or
pool historical runs. Preserve the harness's absolute ceilings: every measured
B operation ≤2500 ms and ≤64 allocated B/pixel, process peak ≤2 GiB.
An unexplained material regression blocks acceptance.

## Preserved initial failure and replacement allocation

The original protocol was posted in
[6359702812](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1541#issuecomment-6359702812)
and committed at `735bdee6`. Its first baseline/candidate pair failed the
zero-wrong-eligible-ID assertion for both profiles. The retained scene selected
200 stars under the current operator default, whereas independent truth used
all 1,637 catalog entries. It included `46518` (magnitude 3.6) and excluded
neighbor `118632` (4.65); nearest-full-catalog truth assigned the unrendered
neighbor. This was not an equivalent full-depth qualification workload.

Remaining repetitions were stopped, no initial 21-case run started, and that
pack receives no acceptance credit. A focused configuration test first failed
with `OperatorSceneObjectLimit`, before the harness correction. The replacement
protocol was posted in
[6360029027](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1541#issuecomment-6360029027)
before replacement measurement: explicitly select `Exhaustive` in this MSA
harness only, retain the full independent oracle and every original bound,
record full configuration and verify durable scene depth. Both source arms
use the identical corrected harness, committed separately on the baseline;
record the actual overlay commit and its unchanged baseline production tree.
The replacement allocation is five fresh pairs plus one accuracy run with the
same timeouts and rules. The failed initial pack and interrupted driver remain
distinct. This does not change shipped profile defaults or assert a producer
correction from the failed nearest-unrendered-star comparison.

This is the existing **tc0 side cell**, `DOTNET_TieredCompilation=0`, Release,
SDK 10.0.402; production latency and production-default JIT claims are excluded.
Other JIT/GC tuning and collectors are excluded. Capture runtime, OS/CPU/memory,
GC mode, catalog and all fixture/configuration/source hashes. Raw private host
metadata stays in the retained evidence; public reports omit private addresses,
hostnames and secrets.

## Finite execution

All heavy work is serial under `flock -o /tmp/hvo-heavy-gate.lock timeout …`.
Build/test commands append `-nodeReuse:false -p:UseSharedCompilation=false`.
Allocate one baseline Release build (1200 s), ten cost processes (600 s each),
one final-pixel process (900 s), and the Tier C gate below. No automatic
performance rerun or retrospective threshold change is authorized by a failure.
Keep the baseline checkout in registered #1541 scratch and new evidence under
`/home/roys/development-state/HVO.SkyMonitor/evidence/1541`; never change the
immutable #1540 pack or its workspace.

For each arm, build the CameraAgent test project warning-clean in Release, then
run each method separately in a fresh output directory:

```bash
env -u HVO_PRODUCTION_CATALOG_BUNDLE \
  DOCKER_HOST=unix:///tmp/hvo-no-docker.sock \
  HVO_ASTROMETRY_CATALOG_ROOT="$catalog" \
  HVO_MSA_ACCEPTANCE=1 DOTNET_TieredCompilation=0 \
  HVO_EVIDENCE_REVISION="$sha" HVO_MSA_OUTPUT="$out" \
  HVO_MSA_WORK_ROOT="$work" \
  flock -o /tmp/hvo-heavy-gate.lock timeout 600 \
  dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj \
  --no-build --configuration Release \
  --filter FullyQualifiedName~MeasuredStellarAssociationAcceptanceTests.RecipeStageCost \
  --results-directory "$out/trx" --logger 'trx;LogFileName=cost.trx' \
  -nodeReuse:false -p:UseSharedCompilation=false
```

For accuracy use its method name, a fresh directory and timeout 900. Each run
must have exit 0, exactly one passed test and no skipped/failed tests, complete
case/operation manifests and no harness failures. Seal raw JSON, TRX, logs,
catalog/source/binary manifests, driver and analysis with SHA256 inventories.

## Local closure

Run `scripts/ci:classify pull_request <base> <candidate>` and retain its output.
Tier C requires tool restore, solution restore, Debug/Release warnaserror,
format verification, package audit, all four CI-control guards, complete
Docker-disabled Unit with `HVO_PRODUCTION_CATALOG_BUNDLE` unset, separate Docker
Integration, canonical selected-component coverage and architecture/migration
checks. Each category/project owns a fresh result slot. Failed receipts never
enter coverage. New test counts update the project inventory and CI runbook.
The protected profile is **development-v1 / Preflight + Build and Unit**;
classifier output does not authorize dispatching `ci.yml`.
