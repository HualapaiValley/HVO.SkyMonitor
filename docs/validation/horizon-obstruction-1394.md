# Virtual horizon-obstruction acceptance (#1394)

Source candidate: `d930b3dd7f6b6d1a2f3d48c791a8b6f532cf0c9e`.
Initial target/merge base: `7d5ec63b09a67ab4b675ba6a29641013df1247d8` (`development/v1`).
Tier C; protected profile `development-v1`. Final synchronization and protected CI
are held behind the operator's #1727 reservation/queue. Merge is not authorized.

## Shared behavior and ownership

The [frozen contract](../contracts/horizon-obstruction-v1.md) owns conventions,
bounds, unknown/gap handling, immutable identities and library/host ownership.
AgentCore carries the profile inside immutable rig revisions; Astronomy evaluates
angular eligibility; Imaging generates separate obstruction/unknown/outside bit
planes; Processing authenticates source-bound products and filters annotations;
CameraAgent owns the manual editor and existing SQLite activation/history path.

Versioned sites bind both coordinates and retained location ID/version. Changing
site revision at identical coordinates invalidates compatibility. Geometry binds
sensor, optics/calibration, orientation and readout; profiles must be explicitly
revalidated after changes. A profile absent from an older rig preserves baseline
behavior. An active empty profile is unknown, never clear sky.

Raw pixels, original orbital tracks and projected-scene evidence remain intact.
Legacy half-open rectangles remain separate. Fixed obstruction and unknown
terrain add typed hard exclusions; calibration/bad pixels, bounds, saturation,
star rejection and dynamic clouds keep their own identities. There is no global
or persistent mask cache. Each generation is limited to 16,777,216 output pixels,
720 angular samples and three bit planes, with row cancellation.

Capture overlays conservatively withhold a whole track with an ineligible point
or segment; planning arcs split at those boundaries. Display segment tests use
linear altitude and shortest azimuth, testing every crossed knot and interval
midpoint; they do not assert physical intersample trajectories. Separately image-
registered or differently sized scene projections are withheld from horizon-filtered
star annotation until alignment is validated, rather than reusing incompatible pixels.

## Acceptance evidence

| Criterion | Evidence |
| --- | --- |
| Bounded host-neutral installation/site/orientation/calibration profile | `HorizonObstructionProfile`, immutable sample snapshot, geometry/site identities; invalid geometry and limits tests |
| Frozen representation, unknown rules and ownership | Contract above; north wrap, null knots, empty/single profiles, oversized gaps, exact and very fine angles |
| Create/edit/preview and explicit activation; retained history | Camera & Rig editor tests and `HorizonObstructionStoreIntegrationTests`: preview/save preserve active state; stage/virtual initialization/rollback preserve archived revisions; failure leaves no partial history |
| Source/profile/calibration identity and incompatible geometry rejection | Mask/prediction binding tests; source changes alter identities; site ID/version, calibration, pointing, flip, sensor and readout changes reject reuse |
| Compatible exclusion combination and raw retention | Real synthetic star/meteor/satellite extraction with unchanged raw bytes; transient worker tests retain rectangle boundaries and stable temporal mask identities |
| Wrap, gaps, crossings, models, transforms, revisions | All five projector families; crop/bin/mirror/quarter rotations; numerical roundtrip ≤1e-8 pixel; peaks and unknown gaps cannot bridge clear endpoints; SQLite rollback and publication fault |
| Bounded generation/caching and full-resolution evidence | Measurements below; exact packed-mask equality/checksums, cancellation and area-limit tests; no retained cache |

The change adds 30 Unit cases, 3 Integration cases and 1 Manual measurement case.
All are virtual/software evidence; physical survey accuracy and camera/ARM cadence
qualification are outside this acceptance.

## Local candidate gates

SDK 10.0.402, Ubuntu 24.04.5 x86_64, Intel Core i9-14900K, 12 exposed CPUs,
15,406 MiB host RAM. Native Release test hosts; Integration uses isolated
Testcontainers and SQLite/filesystem fixtures. No production host was restarted
or deployed. No networking or secret configuration was changed, and no branches
were deleted.

Selector: `scripts/ci:classify pull_request <merge-base> <head>` returned
`mode=full complete=false shared=true cameraagent=true logichost=true combined=true
delivery=true deployment=false secret_scan_contract=false release_scripts=false`.
The full local matrix is required by Tier C and the operator; this selector does
not authorize the full protected workflow for the v1 target.

- Tool restore and solution restore: passed.
- Debug and Release solution builds with `-warnaserror`: passed, zero warnings/errors.
- Whole-solution `dotnet format --no-restore --verify-no-changes`: passed.
- Package audit, Bash syntax, CI classification, coordination and PR-review guards: passed.
- Operations documentation audit: passed.
- Full Docker-disabled Unit selection with coverage: 15,056 passed, 3 conditional skips, 0 failures (15,059 selected).
- Full Integration selection with coverage: 1,010 passed, 0 skips/failures.
- Manual full-resolution measurement: 1 passed.

Unit ran with `DOCKER_HOST=unix:///tmp/hvo-no-docker.sock` and the approved local
`HVO_PRODUCTION_CATALOG_BUNDLE=/home/roys/.cache/hvo-catalog/hyg-v4.2-p3-s2-r1.bundle`.
Conditional skips are the macOS-only rejection, unreachable provider delete ambiguity,
and optional HYG 4.4 cross-lineage transition. None is a horizon test or production
qualification credit. The earlier superseded Integration run was deliberately
canceled after source changes and is not credited.

Commands are the complete Tier C block in AGENTS.md; tests use
`--no-build --configuration Release` with separate `--filter "TestCategory=Unit"`
and `--filter "TestCategory=Integration"` selections, `tests/coverage.runsettings`, and XPlat Code Coverage.
Raw TRX/Cobertura evidence is under `TestResults/issue-1394/final/` (ignored).

## Full-resolution numerical and cost evidence

Reproduce after the Release build:

```bash
dotnet test tests/HVO.SkyMonitor.Processing.Tests --no-build --configuration Release \
  --filter "FullyQualifiedName~HorizonObstructionPerformanceTests" --logger trx
```

Synthetic flat 30-degree horizon, configured fisheye, pixel-centre samples,
concurrency 1. The sizes match W1/W2 dimensions; this is a component measurement,
not the canonical end-to-end W1/W2 campaign. One warmup and three equivalent trials
per path. Reference is the nearest simple flat-cut implementation: unproject each
pixel and set one exclusion plane. After evaluates the bounded angular profile
and returns three separately typed planes. Their union must match the reference
byte-for-byte on every trial. No filesystem, database or network I/O or queue/backlog
exists in the measured operation. Logging and result serialization are outside timing.

| Pixels | Reference median ms | After median ms (min–max) | CPU median ms, reference / after | Allocated bytes median, reference / after | Peak process RSS bytes |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1936x1216 | 42.440 | 87.957 (87.675–90.696) | 42.926 / 91.222 | 294,296 / 883,952 | 107,266,048 |
| 4096x4096 | 462.130 | 977.790 (972.734–995.287) | 521.860 / 988.534 | 2,097,176 / 6,292,592 | 137,056,256 |

4K output storage is exactly 6,291,456 bytes (three 2 MiB planes); 1936×1216 uses
882,816 bytes. Process RSS includes the test host and previous allocations.
The added lookup and separate unknown/outside planes explain the opt-in cost;
there is no additional horizon scan when no profile is active. The 4K reference
trial range was 461.057–826.934 ms, so these three trials establish an absolute baseline,
not a tail percentile or claimed throughput improvement. No production cadence
or physical-target budget is inferred.

Union SHA-256 on every reference/after trial:

- 1936×1216: `6B18FC4A89D45226C44A2AE135D55C0FE112044B4695AEB4AC13C79292D18511`
- 4096×4096: `62521C6530FF6EC1C467D916653F88FAA53E93EADC49900E74BD5D8AF6527565`

Performance fixture profile hashes, CPU/allocations and each trial are retained in
the measurement TRX. Coordinate roundtrips, product identities, canonical retained
inputs and durable profile history are independently asserted by the regression suite.
