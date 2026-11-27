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
- **Opt-out compatibility is exact.** When `RenderDeepSkySources` is omitted (`null`) or disabled
  (`Enabled = false`), it is excluded from scene-identity serialization, all DSO work is bypassed,
  and `VirtualSky` preserves exact prior `SceneId`, `stellarNoiseIdentity`, and raw pixel bytes
  even with stochastic sensor noise enabled. When enabled, `SceneId` and
  `SceneProvenance.DeepSkyCatalog` bind the active DSO catalog snapshot and render-model identity
  independently of annotation selection.

## Shared Geometry, Antipodal Safety, and Clipping (#1191)

### Production Hardening

- **`DeepSkyOutlineSampler`** (`HVO.SkyMonitor.Astronomy`) provides shared antipodal-safe
  great-circle outline validation, constraint-isolated curve clipping, and exact gnomonic
  tangent-plane polygon projection (mapping great-circle edges to exact straight lines with zero
  RA-wrap or celestial-pole seam artifacts).
- **Antipodal and near-antipodal edge refusal:** Consecutive ring vertices separated by
  `>= π - 1e-5 rad` (within `1e-5 rad` / `~0.00057°` of `180°`) have no unique great-circle geodesic.
  `DeepSkyOutlineSampler` refuses to invent an arbitrary arc, returning
  `DeepSkyOutlineSampleStatus.InvalidAntipodalEdge` with edge diagnostics. A single invalid outline
  never aborts sibling objects in a scene: rendering falls back to finite catalog ellipse/circle
  geometry when available or skips that object honestly (`annotation-only-invalid-antipodal-outline`).
- **Frozen v2 vs boundary-safe v3 sampling (`ResolvedFootprintSampler`):**
  - `ResolvedFootprintSampler.Sample` (`resolved-footprint-limb-adaptive-v1`) remains byte-frozen
    for legacy `projected-scene-v2` solar-system footprints.
  - `ResolvedFootprintSampler.SampleBoundarySafe` (`resolved-footprint-limb-adaptive-v2`) is the
    versioned clipping sampler used for `projected-scene-v3` deep-sky extents and shared by
    `DeepSkyOutlineSampler`. It isolates geometric horizon (`Up >= 0`), optical projection domain
    (`cz >= minDomainCosine`), and sensor/aperture boundaries sequentially using Lipschitz exclusion,
    60-iteration golden-section extremum search, and 52-bit inside-bracket bisection — preserving
    arbitrarily narrow horizon/domain grazes while never fabricating exterior connecting chords.
- **Explicit sourced contour parity (`projected-scene-v3` & presentation):** OpenNGC query
  selections (`OpenNgcDeepSkyOptions`) support bounded opt-in sourced contours via
  `MaximumOutlines` (`0..32`, default `0` for legacy curated presets) and `PreferredOutlineLevel`
  (`1..3`). When enabled, `DeepSkySceneProjector` emits `DeepSkyRepresentation.Outline` entries and
  populates `ProjectedDeepSky.Outlines` (including outline-only catalog entries with null axes),
  validated in `ProjectedSceneContracts` and rendered by `PresentationDeepSkyLayerProducer` as
  `PresentationDeepSkyDrawing.Outline`.
- **Edge-aware spatial index (`DeepSkyCatalog`):** Spatial reach includes great-circle edge
  midpoints and assigns conservative `180°` reach to antipodal edges so region queries never drop
  objects whose long great-circle edges cross the search cone.
- **Single-extent overflow isolation (`DeepSkySceneProjector`):** Structural polyline overflow on
  one extent degrades only that object (`FootprintLimit`) rather than aborting sibling objects.
- **Projected-scene schema compatibility:** `projected-scene-v1` (stars only) and
  `projected-scene-v2` (solar-system footprints only) remain byte-identical. `projected-scene-v3`
  carries deep-sky sections (including optional sourced outlines) and fails closed in older or
  mismatched readers across `ProjectedSceneJson`, `ProjectedSceneRecipe`, and
  `SqliteCaptureProcessingStore` (which rejects conflicting content or source-lineage reinsertion
  under the same `OutputIdentitySha256`).

### Independent Analytic Clipping Oracle & Error Budget

`tests/HVO.SkyMonitor.Astronomy.Tests/IndependentAnalyticClippingOracle.cs` implements a
standalone clipping oracle with zero imports from `HVO.SkyMonitor.Astronomy` projection,
coordinate, or clipping code:

- Implements IAU 1976 Lieske precession matrices, Greenwich/local mean sidereal time, equatorial
  to horizontal ENU rotation, Bennett 1982 standard-atmosphere refraction, all five projection
  models (`EquidistantFisheye`, `EquisolidFisheye`, `OrthographicFisheye`, `StereographicFisheye`,
  `Perspective`), the exact implemented single-term radial distortion law
  (`n_d = n * (1 + k1 * |n|^2)`, `RadialDistortionK1`, invertibility domain `1 + 3*k1*|n|^2 >= 0.5`),
  and emitted-image crop, bin, mirror, quarter-rotation, and preview transforms directly from
  primary formulas.
- Isolates geometric horizon, optical projection domain, and pixel/aperture/crop constraints in
  three separate stages using closed-form great-circle plane/cone roots (`a·cos θ + b·sin θ = c`),
  Lipschitz exclusion + 60-iteration golden-section extremum search, and 52-bit bisection refinement
  so domain failures never mask narrow horizon grazes.
- Evaluates **continuous bidirectional segment-to-curve Hausdorff distance** (dense `<= 0.02 px`
  stepping plus 28-iteration golden-section local-maximum refinement), **bidirectional open-arc
  endpoint residuals**, and **part/closed topology equality**, verified by negative controls
  including the `[0, 1]` vs `[0, 0.17] + [0.73, 1]` sub-pixel bridged-gap probe (`0.28 px > 0.27 px`).

| Metric | Documented Numerical Error Budget |
| --- | --- |
| Continuous bidirectional Hausdorff distance (`ContinuousHausdorffBudgetPixels`) | `<= 0.27 px` (`0.25 px` chord sagitta contract + `<= 0.02 px` boundary/rounding margin) |
| Vertex-to-curve distance (`VertexOnCurveTolerancePixels`) | `<= 2e-3 px` |
| Bidirectional open-endpoint residual (`BoundaryEndpointResidualBudgetPixels`) | `<= 2e-3 px` |
| Transformed coordinate parity (`TransformParityBudgetPixels`, ROI/bin/mirror/rotation & `PreviewTransform.Apply`) | `<= 1e-8 px` |
| Part & closed-loop topology (`TopologyMatches`) | Exact part count and closed-part count match |

### Carried-Over `#1191` Disposition Summary

See [`docs/planning/requirements-crosswalk.md`](../planning/requirements-crosswalk.md) for the full
current-head table mapping `C1191-01` through `C1191-06` (cross-referencing
`#524#issuecomment-6806453915`) to production owners, concrete callers, and verified behavioral
tests (antipodal refusal, spatial edge reach, frozen v2 Moon vs boundary-safe v3 horizon graze,
independent oracle negative controls & `OrthographicFisheye` domain crossing, sourced OpenNGC
contour parity including outline-only sources, and SQLite archived replay execution + conflict
rejection).

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
- **Spectral weights:** For continuum stellar-population classes (`Galaxy`, `GlobularCluster`, and
  `OpenCluster`), when both catalog `B` and `V` magnitudes are present, relative RGB/CFA channel
  factors derive from `B - V` (`DeepSkySpectralOrigin.CatalogBMinusV`); otherwise explicit class
  simulation defaults (`SimulationClassDefault`) apply. Emission/reflection nebulae, planetary
  nebulae, supernova remnants, and `Cl+N` composites use versioned class simulation defaults
  (`SimulationClassDefault`) unless overridden by a `DeepSkySyntheticScenario` (`SyntheticScenario`),
  and dark nebulae are attenuation-only (`AttenuationOnly`).

## Optical & Sensor Pipeline Integration

1. **Deterministic order-invariant candidate ranking:** Candidates (merged with any synthetic
   `AdditionalObjects` before truncation) are sorted deterministically by Messier number, visual
   magnitude, major-axis extent, and ordinal `Id` before applying `MaximumCandidates`. Host catalog
   queries use an explicit upper bound (`VirtualSkyCameraModule.MaximumDeepSkyCatalogQueryResults = 16,384`).
2. **Pre-clipping flux normalization:** Each luminous source integrates its solid-angle-weighted
   profile (`sin(r)/r` on the exponential map; `(1 + u^2 + v^2)^(-3/2)` on gnomonic outlines) over
   its **full** sky extent before horizon, projection, PSF, aperture, or sensor clipping. Energy
   landing outside the horizon, optical domain, aperture, or sensor is lost and never renormalized.
3. **HYG deduplication before unresolved fallback:** `OpenCluster` and `CompositeClusterNebula`
   always match and deduct rendered HYG star flux inside their footprints BEFORE selecting
   unresolved point-source vs resolved member rendering, eliminating double-counting even for
   compact clusters lacking a minor axis.
4. **Exposure-integrated unresolved convergence:** Unresolved non-diffuse emitters
   (`<= 0.5 * PsfSigmaPixels` or point-source geometry) reuse `StellarExposureGeometryBuilder` and
   `TemporalPointSpreadRaster` across the full exposure interval (`ExposureStartUtc` ..
   `ExposureStartUtc + ExposureSeconds`), matching sidereal motion, horizon crossings, and
   instantaneous moving-cloud transmission identically to the stellar PSF path. Unresolved diffuse
   emitters (`DiffuseNebula`, `SupernovaRemnant`, and `Cl+N` nebula portions) deposit their
   temporal samples into the pre-PSF diffuse map so overlapping dark nebulae attenuate them cleanly.
5. **Admission bound to render options:** `EvaluateSignalAndFootprint` evaluates expected signal,
   complete output-bin noise variance (`CompleteBinSupport`), declared celestial diffuse background,
   RGB `ChannelResponse` & `WhiteBalance`, Bayer CFA response, cloud transmission/scatter,
   vignetting, and source-caused saturation bound to the exact normalized `LinearSceneRenderOptions`
   validated by `DeepSkyRenderPlan.ValidateFor`.
6. **Dark-nebula subpixel-area multiplicative attenuation & post-attenuation PSF:** Dark nebulae
   integrate local solid-angle-to-pixel-area (`px^2/sr`) coverage into padded cells so uncovered
   subpixel background radiance is preserved, compose transmissions multiplicatively
   (`T_total = prod(1 - alpha_i)`) across stacked clouds, respect `MaximumRenderedSources`, and
   convolve `E * T - Bg_diffuse * (1 - T)` once with the configured PSF kernel after Pass 2.
7. **Existing `#518`/`#522` sensor order & final-coordinate diagnostics hash:** `DeepSkyPreCloudRate`
   enters `RenderCore` before cloud attenuation while `DeepSkyTemporalRate` enters after cloud
   attenuation (alongside `StellarExposureRenderPlan`), followed by vignetting, exposure/gain,
   shot/dark/read noise, full-well saturation, RGGB CFA sampling, defects, digital readout binning
   (`MonoDigitalReadoutRenderer`), and quantization. `MonoDigitalReadoutRenderer` recomputes
   `DeepSkyStatistics.DiagnosticsSha256` over the final delivered (binned) diagnostics payload.

## Bounded Work Budgets and Measured Resource Profile

`DeepSkyRenderSettings` enforces hard deterministic caps per capture (honoring all accepted minima
cleanly without throwing, while rejecting invalid merged synthetic geometry/outlines):

- `MaximumCandidates`: default `128` (range `1..2048`)
- `MaximumRenderedSources`: default `64` (range `1..512`, charged across both luminous and dark sources)
- `MaximumSparsePixels`: default/max `524,288`
- `MaximumSamplePointsPerSource`: default `65,536` (range `16..262,144`)
- `MaximumTotalSamplePoints`: default `2,000,000` (range `16..8,000,000`, charged across all paths including unresolved sources)
- `MaximumKernelCellVisits`: default/max `67,108,864`
- `MaximumOutlineEdgeVisits`: default/max `16,777,216` (checked before outline rasterization loops)

### Representative Full-Resolution Measurement (`1936 × 1216` Mono16, ASI174MM-class)

Measured via `VirtualSkyExtendedDeepSkyTests.FullResolutionAsi174DeepSkyCaptureMeasuresBoundedLatencyCpuAllocationsAndCounts`
comparing matched **baseline (`RenderDeepSkySources = null`)** vs **enabled (`RenderDeepSkySources.Enabled = true`)**
with 7 multi-class deep-sky sources (`M31` galaxy, `M42` composite `Cl+N` with catalog outline,
`M45` open cluster with HYG star deduplication, `M13` globular cluster, `M76` unresolved planetary
nebula, `NGC 2023` missing-photometry control, and `LDN-M42` dark nebula overlapping `M42`) at
`SamplesPerProjectedPixel = 2` (5 timed captures each after warmup, Release build on `osx-arm64`):

| Metric | Baseline (Disabled) | Enabled (7 DSOs) | Delta / Bound |
| --- | --- | --- | --- |
| Candidate / eligible / admitted sources | `0` / `0` / `0` | `7` / `6` / `6` (`1` annotation-only, `1` unresolved PSF, `1` dark attenuation) | Caps: `128` / `64` |
| Total sampled points / sparse pixels / PSF kernel visits | `0` / `0` / `0` | `1,588` / `1,064` / `25,738` | Caps: `2,000,000` / `524,288` / `67,108,864` |
| Serialized `deepSkyDiagnostics` metadata size | `0 B` | `8,093 B` | Bounded (`< 64 KiB` for 7 sources) |
| Stellar projection / prediction / reference scene (median wall) | `0.02 ms` / `0.06 ms` / `0.02 ms` | `0.01 ms` / `0.06 ms` / `0.02 ms` | Unchanged |
| DSO candidate select / total DSO prepare (incl. select) / diagnostics serialize (median wall) | `0.00 ms` / `0.00 ms` / `0.00 ms` | `0.01 ms` / `4.20 ms` / `0.03 ms` | `+4.23 ms` DSO pipeline work |
| Sensor plane render (median wall) | `37.49 ms` | `35.40 ms` | Sparse scatter avoids per-pixel lookup overhead |
| Full `1936 × 1216` capture latency (median wall) | `37.79 ms` | `40.03 ms` | `+2.24 ms` |
| Full `1936 × 1216` capture CPU time (mean process CPU) | `40.54 ms` | `43.10 ms` | `+2.56 ms` |
| Mean managed allocations per full-frame capture | `22.56 MiB` | `23.31 MiB` | `+0.75 MiB` |
| Sampled process working set (start / sampled peak across capture loop) | `251.3 MiB` / `296.3 MiB` | `232.3 MiB` / `341.5 MiB` | Sampled between captures (not continuous RSS tracing) |
