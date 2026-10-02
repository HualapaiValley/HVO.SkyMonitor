# Measured stars and astrometric residual diagnostics — v1

Issue #1103 (V4 of #1098) delivers the star-measurement and association-diagnostics subset of #526 that the
virtual HYG all-sky milestone needs. It extends the [shared astrometry library](shared-astrometry-v1.md) with
a second, additive measurement algorithm and a residual-diagnostics product. Annotation policy, diagnostic
presentation and broader host reconstruction stay with #526. No host activation, schema, projected-scene
storage (#1055) or calibration write-back is included, and the v1 `StellarDetector` stays the hosts' path.

## Ownership

- **`HVO.SkyMonitor.Imaging`: `StellarSourceMeasurer` (`linear-stellar-local-v2`).** Measures linear
  monochrome samples or reconstructed CFA luminance with a validity mask and a separate saturation mask.
- **`HVO.SkyMonitor.Astronomy`: `AstrometricResidualAnalyzer`.** Analyses one accepted or rejected solve
  against its calibration, catalog selection, measurements and exclusions, and produces
  `AstrometricResidualDiagnostics` (`astrometric-residual-diagnostics-v1`).
- **No new reference.** The analyzer consumes the neutral `AstrometricMeasurementExclusion` and
  `AstrometricPixelCovariance` records, so Astronomy takes no Imaging dependency.

## Measurement (`linear-stellar-local-v2`)

Every threshold is a declared `StellarMeasurementOptions` value. `SettingsIdentity` hashes the algorithm
version together with every option.

- **Local background and noise.**
  - Background is a sigma-clipped median per tile, bilinearly interpolated between tile centers.
    Unavailable tiles are filled from their neighbours and counted.
  - Saturated samples never enter either estimate.
  - Noise follows the declared `NoiseEstimator`:
    - `AdjacentDifferences` (the default) uses the clipped spread of horizontal neighbour differences divided
      by √2. It cancels gradients and vignetting, but it is valid only for spatially independent noise.
    - `ClippedSpread` is required for interpolated CFA luminance. Bilinear reconstruction correlates
      neighbouring samples, so adjacent differences under-read the noise. On the virtual ASI178MC frame they
      read 22 DN instead of 26 DN, which let more than 4,096 noise peaks through the candidate gate.
- **Segmentation and candidates.**
  - Pixels above the local segmentation threshold form eight-connected components.
  - A component is a candidate when its peak passes the local peak gate.
  - Exceeding `MaximumCandidateCount` returns `CandidateBudgetExceeded`, with no partial detections.
- **Measurement window.** The window spans the component's bounding box grown by the window radius. A
  component above `MaximumComponentSamples`, or whose window exceeds `MaximumWindowSamples` (default 4,096), is
  `extended-region`. The window bound caps per-source cost: a sparse one-sample-wide diagonal of 600 samples
  has few component samples but a window of about 370,000 samples.
- **Centroid.**
  - A compact source's centroid is an iterative Gaussian-windowed first moment. If it does not converge within
    100 iterations to 1e-6 px, the source is `centroid-unconverged`.
  - A trailed source (trail length at least `MinimumTrailLengthPixels`) has no unique fixed point along its flat
    ridge, so its centroid is the flux-weighted first moment of its grown footprint (the aperture).
  - Flux, shape, sharpness and the trail length/angle use the source's own footprint grown by the window
    radius. The trail angle is in [-90°, 90°).
  - Samples from other candidate components are withheld from the measurement window.
- **Saturation.** Saturated samples stay valid and are counted. For CFA, `SaturationDilationPixels = 1` also
  counts each saturated photosite's interpolated 3×3 support. Sources with saturated samples are flagged.
  Sources above `MaximumSaturatedSamples` are excluded.
- **Crowding.** Crowding is resolved only after every candidate has been classified. A neighbouring
  candidate at or above `CrowdingPeakRatio` of the source's peak makes the source `crowded` only if that
  neighbour is itself a plausible source. A neighbour classified `hot-pixel-or-cosmic-ray` or `low-snr` cannot
  contaminate the window, because its samples are already withheld.
- **Centroid covariance.** The covariance propagates the local per-sample noise, plus source shot noise when
  `ElectronsPerSampleUnit` is declared, through the window's fixed-point equation. A trailed source's
  covariance is instead propagated through its aperture first moment (`propagated-noise-trail-moment-…`). It ignores interpolation
  correlation, PSF model error and saturation clipping. It is therefore reported as
  `measurement-covariance-only-unvalidated` and must not be treated as calibrated coverage. Validated
  uncertainty belongs to #1105.

**Exclusion reasons** (`StellarExclusionReasons`), checked in order:

| Code | Meaning |
| --- | --- |
| `image-edge` | Measurement window crosses the readout border |
| `extended-region` | Component exceeds `MaximumComponentSamples`, or its window exceeds `MaximumWindowSamples` |
| `background-unavailable` | No background estimate inside the window |
| `masked-aperture` | Window touches masked (invalid) samples |
| `saturated-excessive` | More than `MaximumSaturatedSamples` saturated samples |
| `hot-pixel-or-cosmic-ray` | Single sample, minor sigma below the floor, or peak/flux sharpness above the limit |
| `too-broad` | Minor sigma above `MaximumMinorSigma` |
| `trail-too-long` | Trail longer than `MaximumTrailLengthPixels` |
| `centroid-unconverged` | A compact (untrailed) source's windowed centroid did not converge |
| `blended` | Own footprint holds a second significant peak behind a saddle |
| `crowded` | A plausible neighbouring source is inside the window (see Crowding) |
| `low-snr` | Propagated signal-to-noise below `MinimumSignalToNoise` |

## Residual diagnostics

`AstrometricResidualAnalyzer.Analyze` is deterministic and bounded. Its identity covers:

- the solve assessment
- the measurement inputs
- the catalog selection
- the diagnostics settings (`AstrometricDiagnosticsOptions`)

It reports:

- **Per-association residuals.** Each association gets a pixel and angular residual vector, its fitting or
  verification role, catalog magnitude, radius fraction, azimuth and normalized residual. The normalized
  residual is present only where a measurement covariance exists.
- **Summaries.** Fitting and verification count/RMS bins by `radius-fraction`, `azimuth-degrees` and
  `catalog-magnitude`, each with its count of eligible predictions. `SummarizeByTime` aggregates frames into
  UTC bins, counting frames and mapped frames. It rejects a bin width whose bin index would exceed the
  32-bit index range.
- **Fit quality.**
  - Fitting and verification RMS.
  - The median normalized residual squared.
  - Normal-matrix conditioning, which is `well-conditioned` or names its failure.
  - Spatial occupancy on a grid.
  - Parameter-bound hits.
- **Unmatched predictions.** Every eligible catalog prediction without an association gets one reason, chosen
  in this order:

  | Order | Reason |
  | --- | --- |
  | 1 | `outside-aperture` or `near-edge` (the prediction is ineligible) |
  | 2 | `crowded-prediction` (another prediction within `IsolationPixels`) |
  | 3 | `ambiguous` |
  | 4 | `claimed-by-other` |
  | 5 | `not-associated-by-solver` |
  | 6 | `measurement-excluded:<code>` (a measurer exclusion within `ExclusionRadiusPixels`) |
  | 7 | `offset-measured-source` |
  | 8 | `no-measured-source` |

  `outside-aperture` means the prediction is outside the calibration's sampled aperture, including outside a
  clipped readout. Predictions are projected without sensor bounds so that off-readout stars reach this
  reason instead of disappearing. `near-edge` means within `EdgeMarginPixels` of the readout border.

  `offset-measured-source` means an unused detection lies beyond the association radius but within
  `OffsetSourceRadiusPixels`, typically a merged blob whose centroid sits between two stars. Before this
  reason existed, that case was misreported as `no-measured-source`.
- **Unassociated detections.** Each gets `no-catalog-prediction`, `near-associated-prediction`,
  `near-ineligible-prediction` or `near-unassociated-prediction`.

Projected expectations are never treated as detections. Truth is used only to score completed results in
tests.

## Emulator scenarios (Unit)

`VirtualMeasuredStarScenarioTests` drive the actual `VirtualSkyCameraModule` with a seeded 1,600-star sky plus
8 planted pairs. Each case decodes the final pixels, then measures, solves, diagnoses and scores against
independent truth. Every accepted case must have no false associations.

| Case | Delivered assertion |
| --- | --- |
| Clear | Accepted; diagnostics complete; RMS < 0.5 px; well-conditioned; covariance reported unvalidated |
| Thin cloud | Accepted; median associated flux ratio below 0.95 against clear |
| Opaque obstruction | Detections below 0.8 × clear; obstructed predictions reported `no-measured-source` |
| Noisy | `low-snr` exclusions; measured noise more than 2 × clear |
| Strong vignetting | Outer radius-bin RMS below 0.75 px |
| Saturation | Saturated flags and `saturated-excessive` exclusions |
| Long exposure | Trailed detections with lengths 3–16 px and angles in [-90°, 90°) |
| Clipped readout (ROI) | Off-readout predictions reported `outside-aperture`, none reported `near-edge`; `image-edge` exclusions |
| Hot pixels | Excluded before solving |
| False stars | Measured but `no-catalog-prediction`, never associated |
| Planted catalog pairs | Never associated; `blended`/`crowded` exclusions and `crowded-prediction` |
| Ambiguous companion | The catalog star is never associated, and every reason is a declared ambiguity reason |

**Emulator limitations:**
- **Native ASI174 readout.** The native 12-bit readout rejects clouds, sensor-plane transients and synthetic
  calibration. Cloud, noise, hot-pixel, false-star and ambiguity cases therefore use a declared linear 16-bit
  profile at gain 1. The native readout also ignores `readNoiseStandardDeviation`.
- **Ambiguity outcomes.** Close catalog pairs usually merge into one component, so ambiguity mostly surfaces
  as `blended`, `crowded`, `crowded-prediction` or `offset-measured-source` rather than solver `ambiguous`.

## HYG v1/v2 comparison (Manual)

`VirtualMeasuredStarQualificationTests` measure the same actual-module frame with both algorithms and solve
both against the HYG v4.2 magnitude ≤5 selection (1,637 stars). The test covers all 7 profiles:

- `mono-native`
- `mono-roi`
- `mono-bin2`
- `mono-roi-bin2`
- `mono-mirror`
- `mono-roll`
- `cfa-native`

**Scoring.**
- An eligible truth star is interior, more than 6 px from the border, and more than 12 px from any other
  star. It is recovered when a detection lies within 1.5 px.
- Centroid error uses the truth-correct associations.
- Each unrecovered eligible star records the nearest exclusion reason, or `no-candidate`.
- The output is `virtual-measured-stars.json` (`virtual-measured-stars-v1`).

**Tolerances** were predeclared on #1103 before the first held-out run. Every case must meet all of them:

| Gate | Limit |
| --- | --- |
| v2 measured mapping | Accepted |
| v2 false associations | 0 |
| v2 centroid RMS | ≤ 0.25 px, and ≤ v1 RMS + 0.02 px |
| v2 recall | ≥ v1 recall − 0.02 |
| v2 measurement time | ≤ 2,500 ms, and ≤ 4 × v1 time |
| v2 allocation | ≤ 64 bytes per readout sample |
| Process peak working set | ≤ 2 GiB |
| Window pressure | A 600-sample diagonal streak is `extended-region`, within the time and allocation limits |
| Diagnostics | Reproduce every association |

The allocation, working-set and window-pressure rows were added after review finding R4, declared from the
tuning partition before the corrected held-out run.

**Resource measurement.**
- The harness requires `DOTNET_TieredCompilation=0`, the same pinned JIT configuration as the other
  resource harnesses. Tiered recompilation and PGO otherwise move the v1/v2 time ratio between runs; the
  original held-out run did not pin it.
- Allocation is exact: both measurers are single-threaded, so `GC.GetAllocatedBytesForCurrentThread` covers
  them. CPU time is the process's, so it is attributable only when the harness runs alone.
- Peak working set is the process's lifetime peak, recorded after each measurement. It includes the catalog,
  the virtual module and both measurers' buffers, so it bounds rather than attributes v2's footprint.
- Run one partition at a time with a test filter, for example:

  ```bash
  HVO_EVIDENCE_REVISION=<committed sha> DOTNET_TieredCompilation=0 HVO_ASTROMETRY_CATALOG_ROOT=<catalog> \
    dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests -c Release \
    --filter "FullyQualifiedName~VirtualMeasuredStarQualificationTests.HeldOutMeasuredStars"
  ```

### Tuning partition (Feb 10, seed 110220; not final evidence)

| Profile | Recall v1 → v2 | Centroid RMS px v1 → v2 | Measure ms v1 → v2 | v2 CPU ms | v2 B/sample | Associations v1 → v2 |
| --- | --- | --- | --- | --- | --- | --- |
| mono-native | 0.959 → 1.000 | 0.133 → 0.081 | 61 → 162 | 162 | 16.5 | 475 → 492 |
| mono-roi | 0.974 → 1.000 | 0.128 → 0.080 | 41 → 114 | 115 | 17.1 | 460 → 471 |
| mono-bin2 | 0.842 → 1.000 | 0.193 → 0.070 | 11 → 35 | 35 | 19.0 | 176 → 209 |
| mono-roi-bin2 | 0.812 → 1.000 | v1 rejected (`ambiguous`) → 0.067 | 9 → 31 | 31 | 21.1 | 0 → 197 |
| mono-mirror | 0.973 → 1.000 | 0.131 → 0.083 | 51 → 128 | 128 | 16.4 | 480 → 492 |
| mono-roll | 0.975 → 1.000 | 0.134 → 0.075 | 48 → 125 | 125 | 16.4 | 482 → 492 |
| cfa-native | 0.986 → 0.994 | 0.084 → 0.092 | 637 → 963 | 973 | 51.8 | 657 → 662 |

Three changes came out of tuning, all before the tolerances were declared:
- the CFA `ClippedSpread` noise estimator
- crowding resolution after classification (it moved CFA recall from 0.961 to 0.994)
- a CFA saturated-sample budget of 36, the mono 4×4 budget dilated by the one-pixel CFA support

**Cost.**
- With tiered compilation pinned off, v2 costs 2.5–3.6× v1 time on mono and about 1.5× on CFA, where
  reconstruction dominates. It stays within both the 4× and the 2,500 ms budgets.
- The highest ratios are on the binned profiles, where v1 takes only about 10 ms. v2's fixed per-frame
  background-mesh cost dominates there.
- v2 allocates 16–21 bytes per sample on mono and about 52 on CFA, including the luminance reconstruction.
- Its bounds are the same pixel and candidate limits as v1, plus the per-source window bound.

### Held-out partition

**Run identity.**
- Revision: `aa1ea47625a726e6b8d7b8c06bec1f13f3905400` (the review-correction commit). It supersedes the
  original held-out run at `927be360`, which predated the R1–R4 corrections.
- Runtime: .NET 10.0.12, 12 logical processors, `DOTNET_TieredCompilation=0`.
- Partitions: Jan 15, May 15 and Sep 15 (seeds 110201, 110205 and 110209), all at 08:00Z.
- Catalog: the HYG v4.2 database `B51D18B7…0B9E2`. Catalog identity `e29a02ef…5b60b`; selection identity
  `ce81fa2e…74cb8`.
- Report: `virtual-measured-stars.json`, SHA-256 `648bc8eccfe6be2500603daf3080654f4e078143dfcdfb8fc54d40f6c3477508`.

**Result.** All 21 cases and the window-pressure case met every declared gate, with an empty failure list:
- Every v2 mapping was accepted, and there were no false associations.
- v2 measurement time was 31–944 ms, at most 3.58 × v1 (`05-mono-roi-bin2`). v2 allocated at most 51.8 bytes
  per sample. The process peak working set reached 745 MiB.
- **Window pressure.** The 600-sample diagonal streak on the native mono readout was `extended-region`, with no
  detection. The frame took 444 ms against 451 ms for the same frame without the streak, and allocated
  4.2 bytes per sample.
- Missed eligible stars occurred only on CFA: Jan had 3 `crowded`, 1 `masked-aperture` and 1
  `saturated-excessive`; May had 5 `crowded`; Sep had 1 `crowded`.

| Case | Recall v1 → v2 | Centroid RMS px v1 → v2 | v2 p95 px | Measure ms v1 → v2 | v2 CPU ms | v2 B/sample | Associations v1 → v2 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 01-mono-native | 0.957 → 1.000 | 0.134 → 0.079 | 0.147 | 60 → 164 | 167 | 16.5 | 469 → 489 |
| 01-mono-roi | 0.972 → 1.000 | 0.131 → 0.081 | 0.150 | 42 → 117 | 129 | 17.2 | 461 → 472 |
| 01-mono-bin2 | 0.832 → 1.000 | 0.177 → 0.060 | 0.100 | 11 → 36 | 36 | 19.1 | 162 → 192 |
| 01-mono-roi-bin2 | 0.809 → 1.000 | 0.179 → 0.059 | 0.100 | 9 → 32 | 32 | 21.2 | 150 → 183 |
| 01-mono-mirror | 0.961 → 1.000 | 0.128 → 0.078 | 0.146 | 50 → 126 | 140 | 16.4 | 470 → 489 |
| 01-mono-roll | 0.971 → 1.000 | 0.134 → 0.085 | 0.170 | 52 → 131 | 131 | 16.4 | 476 → 490 |
| 01-cfa-native | 0.985 → 0.993 | 0.082 → 0.093 | 0.167 | 616 → 941 | 955 | 51.8 | 676 → 680 |
| 05-mono-native | 0.966 → 1.000 | 0.129 → 0.079 | 0.160 | 46 → 127 | 127 | 16.4 | 464 → 481 |
| 05-mono-roi | 0.974 → 1.000 | 0.131 → 0.083 | 0.157 | 38 → 115 | 115 | 17.1 | 451 → 464 |
| 05-mono-bin2 | 0.852 → 1.000 | 0.168 → 0.059 | 0.096 | 11 → 34 | 34 | 19.0 | 168 → 196 |
| 05-mono-roi-bin2 | 0.828 → 1.000 | 0.180 → 0.054 | 0.102 | 9 → 31 | 31 | 21.1 | 154 → 186 |
| 05-mono-mirror | 0.971 → 1.000 | 0.131 → 0.089 | 0.152 | 50 → 125 | 125 | 16.4 | 467 → 481 |
| 05-mono-roll | 0.962 → 1.000 | 0.126 → 0.083 | 0.146 | 52 → 125 | 126 | 16.4 | 463 → 481 |
| 05-cfa-native | 0.985 → 0.992 | 0.077 → 0.088 | 0.156 | 618 → 941 | 957 | 51.7 | 645 → 650 |
| 09-mono-native | 0.985 → 1.000 | 0.129 → 0.073 | 0.138 | 48 → 125 | 125 | 16.4 | 461 → 468 |
| 09-mono-roi | 0.976 → 1.000 | 0.126 → 0.081 | 0.146 | 41 → 118 | 118 | 17.1 | 443 → 453 |
| 09-mono-bin2 | 0.828 → 1.000 | 0.190 → 0.091 | 0.101 | 12 → 34 | 34 | 18.9 | 154 → 183 |
| 09-mono-roi-bin2 | 0.821 → 1.000 | 0.178 → 0.060 | 0.107 | 9 → 31 | 31 | 21.0 | 144 → 174 |
| 09-mono-mirror | 0.974 → 1.000 | 0.130 → 0.082 | 0.144 | 52 → 128 | 128 | 16.4 | 456 → 468 |
| 09-mono-roll | 0.981 → 1.000 | 0.134 → 0.087 | 0.160 | 47 → 127 | 128 | 16.4 | 459 → 468 |
| 09-cfa-native | 0.991 → 0.998 | 0.082 → 0.093 | 0.166 | 610 → 944 | 964 | 51.7 | 657 → 662 |

**Reading the result.**
- **CFA centroid RMS.** On CFA, v2 is 0.011 px worse than v1 while recovering more stars. That is within
  the declared 0.02 px allowance. The likely cause is the stars v2 additionally recovers; this has not been
  separately attributed.
- **Mono centroid RMS after the corrections.** v2 mono RMS rose from 0.049–0.079 px at `927be360` to
  0.054–0.091 px. The likely cause is the trailed-centroid correction: noise elongates a few percent of round
  stars past the 3 px trail floor (15 of 400 in the Monte Carlo unit test), and those now take the aperture
  centroid, which is unbiased but noisier. This has not been separately attributed. On `09-mono-bin2` the RMS
  of 0.091 px against a p95 of 0.101 px points to a few outliers. Every case still improves on v1.
- **What the evidence covers.** Simulated pixels only: no physical sensor, lens or sky.
- **Covariance.** The centroid covariance remains unvalidated. Coverage validation is #1105.
