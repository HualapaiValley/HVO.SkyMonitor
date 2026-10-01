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
- **Centroid.**
  - The centroid is an iterative Gaussian-windowed first moment.
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
  `ElectronsPerSampleUnit` is declared, through the window's fixed-point equation. It ignores interpolation
  correlation, PSF model error and saturation clipping. It is therefore reported as
  `measurement-covariance-only-unvalidated` and must not be treated as calibrated coverage. Validated
  uncertainty belongs to #1105.

**Exclusion reasons** (`StellarExclusionReasons`), checked in order:

| Code | Meaning |
| --- | --- |
| `image-edge` | Measurement window crosses the readout border |
| `extended-region` | Component exceeds `MaximumComponentSamples` |
| `background-unavailable` | No background estimate inside the window |
| `masked-aperture` | Window touches masked (invalid) samples |
| `saturated-excessive` | More than `MaximumSaturatedSamples` saturated samples |
| `hot-pixel-or-cosmic-ray` | Single sample, minor sigma below the floor, or peak/flux sharpness above the limit |
| `too-broad` | Minor sigma above `MaximumMinorSigma` |
| `trail-too-long` | Trail longer than `MaximumTrailLengthPixels` |
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
  UTC bins, counting frames and mapped frames.
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
| Clipped readout (ROI) | `near-edge`/`outside-aperture` predictions and `image-edge` exclusions |
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

**Tolerances** were predeclared on #1103 before the held-out run. Every case must meet all of them:

| Gate | Limit |
| --- | --- |
| v2 measured mapping | Accepted |
| v2 false associations | 0 |
| v2 centroid RMS | ≤ 0.25 px, and ≤ v1 RMS + 0.02 px |
| v2 recall | ≥ v1 recall − 0.02 |
| v2 measurement time | ≤ 2,500 ms, and ≤ 4 × v1 time |
| Diagnostics | Reproduce every association |

### Tuning partition (Feb 10, seed 110220; not final evidence)

| Profile | Recall v1 → v2 | Centroid RMS px v1 → v2 | Measure ms v1 → v2 | Associations v1 → v2 |
| --- | --- | --- | --- | --- |
| mono-native | 0.959 → 1.000 | 0.133 → 0.074 | 296 → 628 | 475 → 492 |
| mono-roi | 0.974 → 1.000 | 0.128 → 0.073 | 118 → 250 | 460 → 471 |
| mono-bin2 | 0.842 → 1.000 | 0.193 → 0.057 | 44 → 83 | 176 → 209 |
| mono-roi-bin2 | 0.812 → 1.000 | v1 rejected (`ambiguous`) → 0.053 | 32 → 72 | 0 → 197 |
| mono-mirror | 0.973 → 1.000 | 0.131 → 0.073 | 165 → 302 | 480 → 492 |
| mono-roll | 0.975 → 1.000 | 0.134 → 0.072 | 165 → 303 | 482 → 492 |
| cfa-native | 0.986 → 0.994 | 0.084 → 0.090 | 1268 → 1898 | 657 → 662 |

Three changes came out of tuning, all before the tolerances were declared:
- the CFA `ClippedSpread` noise estimator
- crowding resolution after classification (it moved CFA recall from 0.961 to 0.994)
- a CFA saturated-sample budget of 36, the mono 4×4 budget dilated by the one-pixel CFA support

**Cost.** v2 costs about 2× v1 time, and about 1.5× for CFA, where reconstruction dominates. It stays within
both the 4× and the 2,500 ms budgets. Its bounds are the same pixel and candidate limits as v1.

### Held-out partition

Recorded after the committed held-out run (Jan 15, May 15 and Sep 15, seeds 110201, 110205 and 110209).
