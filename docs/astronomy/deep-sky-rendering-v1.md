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
  independently of annotation selection; when an in-memory `DeepSkyCatalog` reports
  `SnapshotIdentity == null`, `VirtualSky` binds its lazily-cached full canonical content digest
  (`DeepSkyCatalog.ContentSha256`, covering semantics, objects, aliases including cross-references,
  tombstones, and outlines in `O(1)` per capture), and for custom snapshotless `IDeepSkyCatalog`
  implementations `VirtualSky` computes one unified final effective candidate selection via
  `DeepSkyRenderPlan.SelectCandidates` (incorporating any
  `DeepSkySyntheticScenario.AdditionalObjects` promotion before truncation) shared by both
  rendering and identity hashing, deriving a bounded selection-content digest over semantics,
  summary counts, and only those selected candidates and their per-capture cached outlines
  (`<= MaximumCandidates` outline lookups per capture, skipping displaced rows and synthetically
  overridden outlines) so distinct selection-relevant catalog facts never collide on `SceneId`
  while keeping per-frame work strictly bounded.

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
    for legacy `projected-scene-v2` solar-system footprints (pinned by exact base `214e5ffa0`
    payload lengths and SHA-256 digests including the `5,177`-byte clipped Moon
    `71074B38E3FE195B5DE153176DF270AA1B41A8A9B58C7EC01C4B2FC5C2E40763`, `6,183`-byte centered Moon,
    and `5,262`-byte / `6,268`-byte controls).
  - `ResolvedFootprintSampler.SampleBoundarySafe` (`resolved-footprint-limb-adaptive-v2`) is the
    versioned clipping sampler used for `projected-scene-v3` deep-sky extents and shared by
    `DeepSkyOutlineSampler`. It isolates geometric horizon (`Up >= 0`), the Bennett refraction
    altitude floor (`MinimumAltitudeDegrees`, pinning above-floor and below-floor branches to their
    smooth one-sided limits and never stitching across branch breaks), optical projection domain
    (`cz >= minDomainCosine`), and sensor/aperture boundaries sequentially using Lipschitz exclusion,
    60-iteration golden-section extremum search (including between-sample sensor/aperture excursion
    splitting so curved limbs grazing past a rectangular or circular edge between adjacent inside
    samples are split into clipped open arcs rather than bridged inside), and 52-bit inside-bracket
    bisection — preserving arbitrarily narrow horizon/domain grazes while never fabricating exterior
    connecting chords or bridging refraction jumps. When the chord error bound cannot be met within
    maximum subdivision depth, `SampleBoundarySafe` throws `InvalidOperationException` (which
    `DeepSkySceneProjector` catches to degrade only that single extent to `FootprintLimit`), and
    `DeepSkyOutlineSampler.Sample` returns `InvalidGeometry` without aborting sibling objects.
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
  under the same `OutputIdentitySha256`, and preserves exact v3 output identity and payload bytes
  across archived replay executions).

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
- Samples every visible reference arc adaptively to a strict `<= 1e-4 px` chord-sagitta tolerance
  (up to depth 14 with inflection-point quarter-chord guard), self-verified to `< 5e-4 px` on both
  `40 px` and `4,000 px` circles so large projected extents never incur reference-curve chord bias.
- Evaluates **continuous bidirectional segment-to-curve Hausdorff distance** (dense `<= 0.02 px`
  stepping plus 28-iteration golden-section local-maximum refinement with AABB pruning),
  **bidirectional open-arc endpoint residuals**, and **part/closed topology equality**, verified by
  negative controls including the `[0, 1]` vs `[0, 0.17] + [0.73, 1]` sub-pixel bridged-gap probe
  (`0.28 px > 0.27 px`).

| Metric | Documented Numerical Error Budget |
| --- | --- |
| Oracle reference-curve chord sagitta (`ReferenceSagittaTolerancePixels`) | `<= 1e-4 px` (self-verified `< 5e-4 px` up to `4,000 px` radius) |
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
4. **Exposure-integrated unresolved convergence:** Unresolved emitters
   (`<= 0.5 * PsfSigmaPixels` or point-source geometry) reuse `StellarExposureGeometryBuilder` and
   `TemporalPointSpreadRaster` across the full exposure interval (`ExposureStartUtc` ..
   `ExposureStartUtc + ExposureSeconds`), matching sidereal motion, horizon crossings, and
   instantaneous moving-cloud transmission identically to the stellar PSF path. Unresolved `Cl+N`
   composites with both nebular and cluster flux evaluate the shared temporal trajectory once and
   scale the unit raster linearly so per-source sample budgets are never double-charged. Unresolved
   diffuse emitters (`DiffuseNebula`, `SupernovaRemnant`, and `Cl+N` nebula portions) evaluate
   overlapping dark-cloud optical depth `exp(-sum_k tau_k)` in the co-moving geometric sky frame at
   each temporal sample instant so Earth rotation never decouples co-located celestial emitters and
   absorbers, while converging smoothly to the stellar PSF when no dark cloud overlaps.
5. **Admission bound to render options, overlapping diffuse noise & slot backfill:**
   `EvaluateSignalAndFootprint` evaluates expected signal, complete output-bin noise variance
   (`CompleteBinSupport`), overlapping rendered diffuse emission electrons (with immediate
   fixed-point re-evaluation whenever newly admitted diffuse emission overlaps an earlier source,
   excluding a source's own flux, so any S/N demotion frees its `MaximumRenderedSources` slot for
   subsequent eligible candidates in deterministic priority order), declared celestial diffuse
   background, RGB `ChannelResponse` & `WhiteBalance`, Bayer CFA response, cloud transmission/scatter,
   vignetting, and source-caused saturation bound to the exact normalized `LinearSceneRenderOptions`
   validated by `DeepSkyRenderPlan.ValidateFor`.
6. **Order-independent dark-nebula overlap composition, exact work charging & post-attenuation PSF:**
   Connected overlapping dark-cloud components fix their common subpixel density and
   resolved-vs-fine classification purely from the unordered component set
   (`PlanDarkOverlapComponents`), cache per-pixel unprojected subpixel directions and per-cloud
   fine interior samples in canonical geometric order (charging `MaximumTotalSamplePoints` before
   work and never rebuilding prior rasters), and commit exact canonical transmission
   `T(x,y) = exp(-sum_k tau_k(x,y))` without retaining stale earlier estimates — guaranteeing
   strict monotonicity (`T_all <= T_subset`), exact optical-depth additivity
   (`tau1 + tau2 == tau_combined`), order invariance across arbitrary `N >= 2` overlapping
   resolved and unresolved clouds, and exact sample-budget accounting. Before committing
   transmission or consuming a `MaximumRenderedSources` slot, each dark nebula computes
   diffuse-only absorption deficit electrons, null-hypothesis pre-attenuation local variance
   (`CompleteBinSupport`, including co-located foreground non-diffuse DSO and exposure-integrated
   HYG star shot noise strictly charged against `MaximumTotalSamplePoints`,
   `MaximumKernelCellVisits`, and `MaximumSparsePixels` with zero unbounded helpers), and S/N via
   `EvaluateDarkSignalPrediction`, enforcing `MinimumSignalToNoise` and publishing real
   `PredictedSignal` in diagnostics; `E * T - Bg_diffuse * (1 - T)` is convolved once with the
   configured PSF kernel after Pass 2.
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
- `MaximumRenderedSources`: default `64` (range `1..512`, charged across both admitted luminous and dark sources)
- `MaximumSparsePixels`: default/max `524,288` (enforced across the combined union of pre-cloud and temporal sparse pixel maps as well as pre-PSF diffuse/transmission maps)
- `MaximumSamplePointsPerSource`: default `65,536` (range `16..262,144`, enforced across extended rasterization and resolved open-cluster / `Cl+N` member generation)
- `MaximumTotalSamplePoints`: default `2,000,000` (range `16..8,000,000`, charged across all paths including unresolved sources)
- `MaximumKernelCellVisits`: default/max `67,108,864`
- `MaximumOutlineEdgeVisits`: default/max `16,777,216` (checked before outline rasterization loops, cluster-member outline containment queries, and dark-cloud outline optical-depth evaluations)

### Representative Full-Resolution Measurement (`1936 × 1216` Mono16, ASI174MM-class)

#### Raw Rendering Pipeline Overhead (`RenderDeepSkySources`)

Measured in isolation via `VirtualSkyExtendedDeepSkyTests.FullResolutionAsi174DeepSkyCaptureMeasuresBoundedLatencyCpuAllocationsAndCounts`
comparing matched **baseline (`RenderDeepSkySources = null`)** vs **enabled (`RenderDeepSkySources.Enabled = true`)**
with 7 multi-class deep-sky sources (`M31` galaxy, `M42` composite `Cl+N` with catalog outline,
`M45` open cluster with HYG star deduplication, `M13` globular cluster, `M76` unresolved planetary
nebula, `NGC 2023` missing-photometry control, and `LDN-M42` dark nebula overlapping `M42`, where
`LDN-M42` is honestly filtered below `MinimumSignalToNoise = 1.0`) at `SamplesPerProjectedPixel = 2`
(5 timed captures each after warmup, Release build on `osx-arm64`):

| Metric | Baseline (Disabled) | Enabled (7 DSOs) | Delta / Bound |
| --- | --- | --- | --- |
| Candidate / eligible / admitted sources | `0` / `0` / `0` | `7` / `6` / `5` (`2` annotation-only, `1` unresolved PSF, `0` dark attenuation above S/N 1.0) | Caps: `128` / `64` |
| Total sampled points / sparse pixels / PSF kernel visits | `0` / `0` / `0` | `2,056` / `1,064` / `25,003` | Caps: `2,000,000` / `524,288` / `67,108,864` (prior pre-delta4 historical baseline: `1,588` / `1,064` / `25,738`) |
| Serialized `deepSkyDiagnostics` metadata size | `0 B` | `8,126 B` | Bounded (`< 64 KiB` for 7 sources) |
| Stellar projection / prediction / reference scene (median wall) | `0.02 ms` / `0.06 ms` / `0.02 ms` | `0.02 ms` / `0.07 ms` / `0.02 ms` | Unchanged |
| DSO candidate select / total DSO prepare (incl. select) / diagnostics serialize (median wall) | `0.00 ms` / `0.00 ms` / `0.00 ms` | `0.02 ms` / `4.99 ms` / `0.05 ms` | `+5.04 ms` DSO pipeline work |
| Sensor plane render (median wall) | `34.22 ms` | `34.57 ms` | `+0.35 ms` (sparse scatter avoids per-pixel lookup overhead) |
| Full `1936 × 1216` capture latency (median wall) | `34.63 ms` | `39.87 ms` | `+5.24 ms` |
| Full `1936 × 1216` capture CPU time (mean process CPU) | `38.41 ms` | `52.85 ms` | `+14.44 ms` |
| Mean managed allocations per full-frame capture | `22.56 MiB` | `23.54 MiB` | `+0.98 MiB` |
| Sampled process working set (start / sampled peak across capture loop) | `251.1 MiB` / `296.1 MiB` | `234.3 MiB` / `342.5 MiB` | Sampled between captures (not continuous RSS tracing) |

Compared with the earlier pre-delta4 baseline (`1,588` sampled points, `25,738` kernel visits),
the final delta4 deterministic counters (`2,056` sampled points, `1,064` sparse pixels, `25,003`
kernel visits, `8,126 B` diagnostics, baseline/enabled pixel digests `9A27D3E30C4DE05F` /
`CA8CEE0583EF177D`) reflect pre-work charging of the full conservative padded subpixel grid for
dark-cloud overlap evaluation and exact `StellarExposureRenderPlan` footprint culling (with strict
budget charging for overlapping foreground stars).

#### `#1191` Geometry Serialization & SQLite Persistence I/O Overhead (`projected-scene-v1` vs `projected-scene-v3`)

Measured in isolation via `VirtualSkyExtendedDeepSkyTests.FullResolutionAsi174DeepSkyV3GeometrySerializationAndSqlitePersistenceBenchmark`
comparing matched `1936 × 1216` Mono16 captures executed through the real `RawCaptureIngress`,
`StandardCaptureLaneHandler`, `IProjectedSceneStagingStore`, and `SqliteCaptureProcessingStore`
(`projected-scene-v1` baseline vs `projected-scene-v3` with `DeepSkyEnabled = true`,
`DeepSkyMaximumOutlines = 16`, 3 timed runs after warmup, Release build on `osx-arm64`):

| Metric | Baseline (`projected-scene-v1`) | Enabled (`projected-scene-v3`) | Delta / Bound |
| --- | --- | --- | --- |
| Serialized projected-scene payload size | `2,774 B` | `16,090 B` | `+13,316 B` (`< 256 KiB` bound) |
| Projected DSO objects / outlines (parts, vertices) / footprints (parts, vertices) | `0` / `0` (`0`, `0`) / `0` (`0`, `0`) | `6` / `1` (`1`, `4`) / `2` (`2`, `128`) | Within contract point caps |
| Capture + staging latency (median wall) | `42.86 ms` | `48.51 ms` | `+5.65 ms` |
| Ingress + lane processing + SQLite/file persistence latency (median wall) | `30.28 ms` | `31.39 ms` | `+1.11 ms` |
| End-to-end capture + persistence latency (median wall) | `72.07 ms` | `82.75 ms` | `+10.68 ms` |
| End-to-end CPU time (mean process CPU per capture) | `51.04 ms` | `63.93 ms` | `+12.89 ms` |
| Mean managed allocations per end-to-end capture | `24.22 MiB` | `27.04 MiB` | `+2.82 MiB` |
| SQLite database + WAL/SHM footprint on disk (after 4 captures) | `3,131,344 B` | `3,156,064 B` | `+24,720 B` |
