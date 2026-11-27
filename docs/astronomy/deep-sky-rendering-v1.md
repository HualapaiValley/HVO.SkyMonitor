# Extended Deep-Sky Source Rendering & Analytic Clipping Reference — v1

Issue #524 introduces deterministic, opt-in parametric extended deep-sky source rendering
(`deep-sky-parametric-v1`) for `VirtualSky` alongside shared antipodal-safe outline sampling and
an independent analytic clipping reference completing the carried-over #1191 verification work.

## Scope and Boundaries

- **Catalog facts remain immutable and separate from appearance models.** OpenNGC coordinates,
  axes, position angles, magnitudes, outlines, and aliases are never mutated or extrapolated with
  invented values.
- **Simulation parameters are explicitly versioned and labeled.** All parametric morphology and
  default class spectral weights carry `ProfileSchemaVersion = "deep-sky-parametric-profile-v1"`
  and `IsSimulationParameter = true`.
- **Missing compatible photometry or geometry stays annotation-only.** Objects lacking a usable
  integrated V magnitude (or optional `B25` surface-brightness fallback when explicitly enabled)
  or lacking complete sky geometry (for example, an unoriented ellipse with `minor < major` and
  missing position angle and no valid outline) emit zero sensor flux unless an explicit versioned
  `DeepSkySyntheticScenario` (`deep-sky-synthetic-scenario-v1`) supplies overrides.
- **Canonical presentation outlines stay strictly separate from raw imagery.** Enabling or
  disabling `ProjectedScene` / `DeepSky` annotations never alters raw pixel bytes. Measured
  stellar associations (`StellarSourceMeasurer`) continue to derive solely from final rendered
  pixels rather than `DeepSkyDiagnostics`.
- **Opt-out compatibility is exact.** When `RenderDeepSkySources` is omitted or disabled,
  `VirtualSky` preserves prior `SceneId` identities and exact raw pixel bytes.

## Shared Geometry, Antipodal Safety, and Clipping (#1191)

### Production Hardening

- **`DeepSkyOutlineSampler`** (`HVO.SkyMonitor.Astronomy`) provides shared antipodal-safe
  great-circle outline validation, adaptive curve subdivision, and exact gnomonic tangent-plane
  polygon projection (mapping great-circle edges to exact straight lines with zero RA-wrap or
  celestial-pole seam artifacts).
- **Antipodal and near-antipodal edge refusal:** Consecutive ring vertices separated by
  `>= π - 1e-5 rad` (within `1e-5 rad` / `~0.00057°` of `180°`) have no unique great-circle geodesic. `DeepSkyOutlineSampler`
  refuses to invent an arbitrary arc, returning `DeepSkyOutlineSampleStatus.InvalidAntipodalEdge`
  with edge diagnostics. A single invalid outline never aborts sibling objects in a scene:
  rendering falls back to finite catalog ellipse/circle geometry when available or skips that
  object honestly (`annotation-only-invalid-antipodal-outline`).
- **Exterior tangency and boundary bisection (`ResolvedFootprintSampler`):** Adaptive subdivision
  bisects directly on the combined visibility predicate (`TryProjectGeometry && IsInsideProjection`)
  for 40 iterations, inspects curvature envelopes to catch short boundary incursions, and refuses
  to emit fabricated connecting chords between exterior endpoints at leaf depth.
- **Edge-aware spatial index (`DeepSkyCatalog`):** Spatial reach includes great-circle edge
  midpoints and assigns conservative `180°` reach to antipodal edges so region queries never drop
  objects whose long great-circle edges cross the search cone.
- **Single-extent overflow isolation (`DeepSkySceneProjector`):** Structural polyline overflow on
  one extent degrades only that object (`FootprintLimit`) rather than aborting sibling objects.
- **Projected-scene schema compatibility:** `projected-scene-v1` (stars only) and
  `projected-scene-v2` (solar-system footprints only) remain byte-identical. `projected-scene-v3`
  carries deep-sky sections and fails closed in older/mismatched readers across `ProjectedSceneJson`,
  `ProjectedSceneRecipe`, and `SqliteCaptureProcessingStore`.

### Independent Analytic Clipping Oracle & Error Budget

`tests/HVO.SkyMonitor.Astronomy.Tests/IndependentAnalyticClippingOracle.cs` implements a
standalone clipping oracle with zero imports from `HVO.SkyMonitor.Astronomy` projection,
coordinate, or clipping code:

- Implements IAU 1976 Lieske precession matrices, Greenwich/local mean sidereal time, equatorial
  to horizontal ENU rotation, Bennett 1982 standard-atmosphere refraction, all five projection
  models (`EquidistantFisheye`, `EquisolidFisheye`, `OrthographicFisheye`, `StereographicFisheye`,
  `Perspective`), Brown-Conrady radial distortion (`k1`, `k2`), and emitted-image crop, bin,
  mirror, quarter-rotation, and preview transforms directly from primary formulas.
- Isolates visibility transitions analytically via closed-form great-circle plane/cone roots
  (`a·cos θ + b·sin θ = c`), golden-section extremum search for short incursions/excursions, and
  52-bit bisection refinement.
- Evaluates **continuous bidirectional segment-to-curve Hausdorff distance** (subdividing every
  emitted segment so fabricated exterior chords are always caught) plus explicit negative controls.

| Metric | Documented Numerical Error Budget |
| --- | --- |
| Continuous bidirectional Hausdorff distance (`ContinuousHausdorffBudgetPixels`) | `<= 0.27 px` (`0.25 px` chord sagitta contract + `<= 0.02 px` boundary/rounding margin) |
| Vertex-to-curve distance (`VertexOnCurveTolerancePixels`) | `<= 2e-3 px` |
| Boundary transition endpoint residual (`BoundaryEndpointResidualBudgetPixels`) | `<= 2e-3 px` |
| Transformed coordinate parity (`TransformParityBudgetPixels`, ROI/bin/mirror/rotation/preview) | `<= 1e-8 px` |

## Versioned Parametric Source Profiles (`deep-sky-parametric-profile-v1`)

`DeepSkySourceProfileResolver` maps supported catalog object classes to bounded, texture-free,
deterministic profiles (`HVO.SkyMonitor.Imaging/DeepSkySourceProfile.cs`):

| Profile Class | OpenNGC Types | Morphology Model | Notes & Limits |
| --- | --- | --- | --- |
| `Galaxy` | `G`, `GPair`, `GTrpl`, `GGroup` | `galaxy-sersic-smooth-v1` | Truncated Sérsic profile parameterized from coarse Hubble class (`E` -> `n=3.5`, `S0` -> `n=2.6`, `Sa/Sb/SBa/SBb` -> `n=2.0`, `Sc/Sd/Irr` -> `n=1.2`, unknown -> `n=1.8`). No spiral-arm, dust-lane, or bar textures. |
| `GlobularCluster` | `GCl` | `globular-king-granular-v1` | King/Plummer core (`rc = 0.22`) plus bounded (`<= 0.16`) deterministic seeded angular granularity. Not individual resolved member photometry. |
| `OpenCluster` | `OCl`, `*Ass` | `open-cluster-residual-sparse-v1` | Matches rendered HYG stars inside the cluster footprint and subtracts their integrated flux before depositing deterministic residual unresolved members (`{id}:member:{index:D3}`). Prevents double-counting; skips when HYG flux covers `>= 100%`. |
| `PlanetaryNebula` | `PN` | `planetary-nebula-ring-disc-v1` | Ring-and-disc radial profile with `[O III]`/`H-alpha` simulation spectral weights; smoothly converges to the stellar PSF when unresolved. |
| `DiffuseNebula` | `Neb`, `EmN`, `HII`, `RfN` | `diffuse-nebula-soft-envelope-v1` | Smooth bounded envelope (`(1 - q^2)^1.5`) or outline-constrained smooth taper. No fabricated filamentary or survey-image texture. |
| `SupernovaRemnant` | `SNR` | `remnant-limb-shell-v1` | Limb-brightened shell (`r_inner = 0.78`) with smooth outer taper. |
| `CompositeClusterNebula` | `Cl+N` | `composite-cluster-nebula-v1` | Splits flux between diffuse nebula envelope (`75%` default) and HYG-deduplicated residual cluster members (`25%` default). |
| `DarkNebula` | `DrkN` | `dark-nebula-attenuation-v1` | Attenuation-only (`exp(-tau)`). Requires a compatible celestial diffuse background (overlapping diffuse nebula pre-PSF grid or declared `CelestialDiffuseBackgroundElectronsPerSecond > 0`). Never attenuates foreground terrestrial skyglow. |

### Photometry and Spectral Limits

- **Primary photometry:** Integrated visual magnitude `V` mapped through `M0 * 10^(-0.4 * V)`.
- **Optional fallback:** Surface brightness (`SurfaceBrightness`, `B` mag/arcsec² within the 25
  mag/arcsec² isophote) is disabled by default (`AllowSurfaceBrightnessB25Fallback = false`) and
  only used when explicitly opted in and finite geometry exists.
- **Spectral weights:** When both catalog `B` and `V` magnitudes are present, relative RGB/CFA
  channel factors derive from `B - V` (`DeepSkySpectralOrigin.CatalogBMinusV`); otherwise explicit
  class simulation defaults (`SimulationClassDefault`) apply and are labeled as simulation
  parameters.

## Optical & Sensor Pipeline Integration

1. **Pre-clipping flux normalization:** Each luminous source integrates its solid-angle-weighted
   profile (`sin(r)/r` on the exponential map; `(1 + u^2 + v^2)^(-3/2)` on gnomonic outlines) over
   its **full** sky extent before horizon, projection, PSF, aperture, or sensor clipping. Energy
   landing outside the horizon, optical domain, aperture, or sensor is lost and never renormalized.
2. **Unresolved convergence:** Any non-cluster source whose projected major axis is
   `<= 0.5 * PsfSigmaPixels` (or has point-source geometry) routes directly through the exact
   subpixel Gaussian PSF (`DepositDirectSubpixelPsf`), converging smoothly to the existing stellar
   PSF path without arbitrary minimum marker sizes.
3. **Extended rasterization & PSF convolution:** Compact sources (`<= 4 * PsfSigmaPixels`) deposit
   directly via subpixel PSF; larger sources deposit bilinearly onto a PSF-padded grid, convolve
   with the same `SolarDiskRenderPlan.CreateKernel(PsfSigmaPixels, PsfRadiusPixels)` stellar PSF,
   and then clip to the sensor and aperture.
4. **Dark-nebula attenuation pass:** Applied after luminous diffuse rasterization strictly against
   compatible pre-PSF celestial diffuse light and/or declared celestial diffuse background.
5. **Existing `#518`/`#522` sensor order:** `DeepSkyRate(x, y, channel)` enters `RenderCore` in
   incident electron-rate space alongside `BackgroundRate` and `DiskRate` — before cloud
   transmission, radial vignetting, exposure/gain scaling, shot/dark/read noise, full-well
   saturation, RGGB CFA sampling, defects, digital readout binning (`MonoDigitalReadoutRenderer`),
   and quantization.

## Bounded Work Budgets and Measured Resource Profile

`DeepSkyRenderSettings` enforces hard deterministic caps per capture:

- `MaximumCandidates`: default `128` (max `2048`)
- `MaximumRenderedSources`: default `64` (max `512`)
- `MaximumSparsePixels`: default/max `524,288`
- `MaximumSamplePointsPerSource`: default `65,536` (max `262,144`)
- `MaximumTotalSamplePoints`: default `2,000,000` (max `8,000,000`)
- `MaximumKernelCellVisits`: default/max `67,108,864`

### Representative Full-Resolution Measurement (`1936 × 1216` Mono16, ASI174MM-class)

Measured via `VirtualSkyExtendedDeepSkyTests.FullResolutionAsi174DeepSkyCaptureMeasuresBoundedLatencyCpuAllocationsAndCounts`
with 7 multi-class deep-sky sources (`M31` galaxy, `M42` composite `Cl+N` with catalog outline,
`M45` open cluster with HYG star deduplication, `M13` globular cluster, `M76` unresolved planetary
nebula, `NGC 2023` missing-photometry control, and `LDN-M42` dark nebula overlapping `M42`) at
`SamplesPerProjectedPixel = 2` (5 timed captures after warmup, Release build):

| Metric | Measured Value |
| --- | --- |
| Candidate / eligible / admitted sources | `7` / `6` / `6` (`1` annotation-only missing photometry, `1` unresolved PSF, `1` dark attenuation) |
| Total sampled points | `1,586` |
| Total rasterized sparse pixels | `1,064` |
| Total PSF kernel cell visits | `18,362` |
| Deep-sky plan preparation latency (median wall) | `3.29 ms` |
| Full `1936 × 1216` capture latency (median wall) | `95.63 ms` |
| Full `1936 × 1216` capture CPU time (mean process CPU) | `154.41 ms` |
| Mean managed allocations per full-frame capture | `28.20 MiB` |
| Process working set (start / peak) | `269.9 MiB` / `323.1 MiB` |
