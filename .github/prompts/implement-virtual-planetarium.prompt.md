---
name: "Implement Virtual Planetarium"
description: "Implement the Hualapai ASI174MM fisheye planetarium, shared annotations, Stellarium validation, and ASI174MC RGB follow-on."
argument-hint: "Optional: starting work stream or fixture"
agent: "agent"
---

Implement the complete
[virtual planetarium and ASI174 simulator plan](../../docs/projects/virtual-planetarium-implementation.md)
in this repository. Also follow the authoritative
[project plan](../../docs/project-plan.md),
[virtual-camera specification](../../docs/virtual-camera.md), pinned
[legacy reference inventory](../../docs/reference-code.md), and `AGENTS.md`.

This command requests implementation, not another plan. Begin by inspecting the
working tree and current tests, create a task list for every deliverable and gate,
then implement all ASI174MM, shared annotation, Stellarium validation, and
ASI174MC RGB milestones in the documented order. If
`${input:scope:Optional starting work stream or fixture}` is supplied, begin
there only after confirming its prerequisites; continue through all remaining
milestones.

## Fixed decisions

- Observatory fixture: Hualapai Valley Observatory, latitude `35.347`,
  east-positive longitude `-113.878`, timezone `America/Phoenix`, fixture
  elevation `0 m` until a sourced elevation is documented.
- Sensor: ASI174MM-compatible `1936 x 1216`, `5.86 um`, principal point
  `(968,608)` in continuous pixel-edge coordinates, unsigned little-endian
  Mono16 samples, two bytes per pixel, tightly packed rows.
- Initial lens: synthetic zenith-pointing `180 deg` equidistant fisheye with
  image-circle radius `0.98 * min(width,height) / 2`, no canonical flip, and no
  claim of measured Fujinon calibration.
- Primary fixture UTC: `2025-01-15T08:00:00Z`; add the documented rotation and
  second-season fixtures required by the implementation plan.
- HYG: versioned local read-only SQLite snapshot through a shared infrastructure
  adapter, with verified source, CC BY-SA 4.0 attribution/ShareAlike treatment,
  checksum, preprocessing, deterministic query policy, and a small checked-in
  test fixture. Astronomy remains storage-neutral and normal capture works
  offline.
- Stellarium: independent geometry/visual oracle, not a byte-exact golden oracle.
- UI: no Blazor work unless strictly needed to falsify runtime image generation.
- ASI174MC: implement RGB24 compatibility only after ASI174MM and annotations
  pass; do not claim Bayer/raw hardware fidelity.

Do not substitute the current unsourced host coordinates for the fixed Hualapai
fixture. Do not present legacy heuristic noise or conflicting Fujinon presets as
measured hardware behavior. Preserve stable contracts in AgentCore,
storage-neutral astronomy/catalog behavior in Astronomy, concrete SQLite access
in the shared catalog infrastructure adapter, reusable image/sensor algorithms
in Imaging, and edge orchestration in CameraAgent.Common. CameraAgent and
LogicHost must not reference each other or implement private celestial/projection
math.

## Required execution method

1. Establish a clean baseline with focused and full builds/tests. Preserve all
   unrelated user changes.
2. Freeze the minimum transport-neutral rig, optics, scene provenance, and
   projected-object contracts before parallel implementation begins.
3. Translate each acceptance criterion into a test matrix. For deterministic
   behavior, add the smallest failing MSTest, verify the expected failure,
   implement the behavior, rerun the focused test, and expand boundary cases.
4. Implement camera basis and projection context, then catalog snapshot access,
   then visible-scene construction, then Mono rendering, then VirtualSky wiring,
   then shared annotations, then Stellarium validation, then RGB rendering.
5. Never burn labels, constellation lines, display gamma, or tone mapping into
   raw camera frames. Never let annotation independently recalculate a different
   scene.
6. Pair image checksums with numeric object coordinates, centroids, dimensions,
   byte layout, and image statistics. A screenshot that looks correct is not
   sufficient evidence.
7. Use explicit `TimeProvider`, fixed UTC, fixed seeds, temporary directories,
   controlled catalog fixtures, and stable algorithm/version identifiers. Avoid
   wall-clock sleeps, live web dependencies, and test ordering.
8. Add an idempotent Stellarium setup and validation script using pinned Ubuntu
   24.04 packages, Xvfb, Mesa llvmpipe, a fresh user directory, checked-in `.ssc`
   scripts, and ignored output artifacts. Assert selected coordinates within the
   documented tolerance; retain PNGs/diffs/logs only as diagnostics.
9. Normalize Stellarium's native 1024-square fisheye coordinates by the canonical
   image circle into ASI174 sensor coordinates using the formula in the plan.
   Separate analytic, astronomy-model, and screen-rounding tolerances; do not use
   whole-image equality.
10. Keep regular analytic, catalog, rendering, module, and annotation tests in the
   normal CI suite. External Stellarium execution may be a manual/scheduled job,
   but its script and fixture manifests must be automated and reproducible.
11. Update `cameraagent.sample.json` to the exact-quarter `484 x 304` fixture and
    update documentation in the same slices. This incremental slice cannot mark
    phases 1, 2, or 5 complete because the remaining compatibility matrix stays
    outstanding.

## Parallel-agent policy

The primary agent owns contracts, integration, and final validation. Use
background agents proactively when work is independent:

- Use a fast exploration/research agent for exact legacy-source paths, catalog
  provenance, existing APIs, and focused codebase mapping.
- Use a general implementation agent for a bounded Astronomy or Imaging work
  stream only after shared contracts are frozen and file ownership does not
  overlap.
- Use a general validation agent for Stellarium automation and fixture evidence;
  it must not edit production projection math.
- Use a separate review agent after each major slice for numerical conventions,
  architecture boundaries, error paths, performance risks, and missing tests.

Give each agent exact files, non-overlapping ownership, required tests, and the
evidence it must return. Do not delegate the same implementation twice. The
primary agent reviews diffs and reruns all checks; subagent output is not proof.

## Mandatory acceptance evidence

- Exact analytic projection and camera-basis tests at center, cardinal axes,
  horizon, edge, invalid domain, arbitrary boresight, roll, and flip.
- Fixed Hualapai catalog-star fixtures at multiple UTC instants plus a second
  latitude fixture, with expected visibility and pixels independent of the code
  under test.
- Catalog source/license/version/checksum evidence and checksum-failure tests.
- Reduced CI and full `1936 x 1216` Mono16 captures with deterministic bytes,
  checksum, object centroids, and statistics.
- Exposure/gain affects signal statistics without moving geometry.
- Time/orientation changes move stars according to documented expectations.
- Raw/preview/annotation coordinates agree after explicit scaling/transforms.
- Ordinary capture, artifact, storage, latest-frame, telemetry, and upload paths
  operate without simulator-only host branches.
- Stellarium `ProjectionFisheye` coordinate and screenshot diagnostics reproduce
  the canonical fixture after explicit `0.98` viewport normalization, with
  analytic and astronomy-model tolerances recorded separately.
- ASI174MC RGB24 output shares Mono geometry, has documented channel order and
  deterministic color/noise behavior, and is explicitly labeled compatibility
  output rather than Bayer raw.

## Quality gates

Do not delete, skip, weaken, or broadly recategorize tests. Do not add broad
`NoWarn`, reduce analyzers, or hide advisories. Add a checked-in coverage
enforcement command that fails the file-group and aggregate-baseline thresholds
defined in the implementation plan. Public shared APIs require XML documentation
for units, ranges, conventions, failure behavior, ownership, and thread safety.

After each slice run focused tests. At each milestone and before completion run
the exact repository CI sequence:

```bash
dotnet restore HVO.SkyMonitor.v9.slnx
dotnet build HVO.SkyMonitor.v9.slnx --no-restore --configuration Release
dotnet test HVO.SkyMonitor.v9.slnx --no-build --configuration Release \
  --filter "TestCategory!=Integration&TestCategory!=Manual" \
  --settings tests/coverage.runsettings \
  --collect:"XPlat Code Coverage"
```

Also run the broader local gate:

```bash
dotnet build HVO.SkyMonitor.v9.slnx --no-restore --configuration Debug -warnaserror
dotnet build HVO.SkyMonitor.v9.slnx --no-restore --configuration Release -warnaserror
dotnet test HVO.SkyMonitor.v9.slnx --no-build --configuration Release \
  --filter "TestCategory!=Hardware&TestCategory!=Manual" \
  --settings tests/coverage.runsettings \
  --collect:"XPlat Code Coverage"
dotnet format HVO.SkyMonitor.v9.slnx --verify-no-changes --no-restore
dotnet list HVO.SkyMonitor.v9.slnx package --vulnerable --include-transitive
```

Run the Stellarium validation command documented and implemented by this work,
record its exact version/settings/results, and inspect generated diagnostics.
Measure full-resolution render time and allocations. Do not invent a target-host
performance threshold without measurements.

Continue through ASI174MM, annotations, external validation, and ASI174MC. Stop
early only for a genuine external blocker such as an unavailable licensed
catalog artifact or physical-hardware decision that cannot be resolved from the
pinned references. Before stopping, finish every unaffected task and report the
exact blocker, completed evidence, and first resumable action.
