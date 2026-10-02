# Astrometric uncertainty — v1

Issue #1105 (V6 of #1098) publishes a per-frame uncertainty for an accepted astrometric mapping. Each part of
it is a separate, named component, and it is withheld with an actionable reason whenever the evidence cannot
support it. It consumes the [measured-star](measured-star-diagnostics-v1.md) centroid covariance (#1103), the
[session optical calibration](session-optical-calibration-v1.md) covariance (#1104) and the #1095 host clock
facts. It builds on the [shared astrometry library](shared-astrometry-v1.md).

**Not included:**
- proper motion or epoch propagation, which stays with #521
- a parallel clock implementation; clock facts come only from the #1095 kernel view
- host persistence, presentation or FITS/WCS export of the uncertainty
- physical lens or sensor qualification, which stays with #1107/#523

## Ownership

- **`HVO.SkyMonitor.Astronomy`**
  - Owns the estimator: `AstrometricUncertaintyEstimator.Estimate` and `CrossCovariance`, with
    `AstrometricUncertaintyOptions` (`astrometric-frame-uncertainty-v1`) and the result
    `AstrometricFrameUncertainty` (schema `astrometric-frame-uncertainty-v1`).
  - Owns the shared-calibration input `AstrometricCalibrationCovariance` (`astrometric-calibration-covariance-v1`).
  - Owns the clock-facts contract `AstrometricClockFacts`, which is host-neutral.
  - `OpticalCalibrationResult.SharedCovariance` (`OpticalCalibrationCovariance`, basis
    `residual-scaled-marginal-gauss-newton-v1`) now publishes the full marginal covariance of the session optics after
    every per-frame pose is eliminated. It is present only on an accepted result. A result without it keeps its
    pre-#1105 identity.
  - `AstrometricLinearAlgebra` holds the shared small symmetric eigenvalue and positive-definite inverse routines
    that the residual analyzer, the session fitter and the estimator now use.
- **`HVO.SkyMonitor.CameraAgent.Common`**
  - `AstrometricClockFactsMapper` maps the #1095 `ClockSyncSnapshot`/`KernelClockState` into clock facts.
  - It uses only the kernel's synchronization status and its own maximum and estimated error. An SNTP offset is one
    sample against one server and is never reported as a bound.
  - A snapshot taken after the exposure midpoint, or older than the declared maximum age at the midpoint, maps to
    `Unknown`.

## Model

The estimator refits the accepted frame with **fixed associations**. It never re-associates and never changes
acceptance. The heuristic quality score and PSF width are never used as a probability.

- **Parameters.** An ENU rotation vector in radians applied to the estimate, then the log focal scale
  (`enu-rotation-vector-radians-then-log-focal-scale`).
- **Fit.**
  - Generalized least squares, solved by Gauss-Newton, on the fitting associations.
  - Each star's weight is `(C_s + σ_sys² I)⁻¹`, where `C_s` is its measured centroid covariance and `σ_sys` is the
    declared `SystematicPixelSigma`.
  - Derivatives are central differences with step 1e-6.
- **Numerics.**
  - The normal matrix is Jacobi (correlation) scaled before inversion. Its spectral condition number is reported and
    limited by `MaximumConditionNumber` (1e6).
  - A non-positive-definite scaled matrix is `rank-deficient`.
  - A star or focal scale that leaves the projection domain under a derivative step is `derivative-unsupported`.
    This includes a focal scale at the lens family's own limit, for example an orthographic image circle equal to
    its focal length.
- **Components**, each a row-major 4 × 4 covariance:
  - **Conditional fit.** The centroid part of the sandwich `A⁻¹ Jᵀ W (C + S) W J A⁻¹`, with `A = Jᵀ W J`.
  - **Systematic.** The floor part of the same sandwich, plus any declared common-mode centroid offset
    (`CommonModePixelSigma`) propagated as a nuisance. The sandwich split is exact, so these two always sum to `A⁻¹`.
    A zero budget is reported as `declared-zero-budget`. The budget is a declaration, never an omission.
  - **Shared calibration.** The session calibration covariance `P`, propagated through `G = A⁻¹ Jᵀ W L`, where `L` is
    the predicted-pixel response to each calibration parameter through the frame's derived readout view. Its pose
    covariance is `G P Gᵀ`. `Sensitivity` (= `G`, 4 × k) is published so that any two frames under the same calibration
    have cross-covariance `S_a P S_bᵀ` (`CrossCovariance`). Frames under different calibration covariances are rejected
    rather than assumed to be uncorrelated.
  - **Total.** Conditional + systematic + shared calibration. It is withheld unless all three are available, so a
    frame without a supplied calibration covariance never presents a conditional covariance as its total.
  - **Clock.** A bound, never a probability, and never added to the total (see Clock policy).
- **Model validity.** Both tests use `ModelValidityAlpha` (1e-3). Quantiles use Wilson–Hilferty with Acklam's
  normal quantile.
  - Fitting χ² with 2N − 4 degrees of freedom. The shared calibration and common-mode nuisances are profiled out
    exactly for the linearized model, and the degrees of freedom stay 2N − 4.
  - Held-out prediction χ² on the verification associations, which are never used in the fit. Their residuals share
    the pose estimate and the nuisances, so the covariance `D + Z Ω Zᵀ` is not block diagonal. A Woodbury form keeps the
    solve at 4 + q wide.
  - Failing either test withholds every covariance component. The conditional estimate is still reported, and the
    centroid status becomes `inconsistent-with-residuals`.

## Withhold reasons

| Code | Meaning and action |
| --- | --- |
| `assessment-not-accepted` | No accepted measured mapping |
| `focal-scale-at-search-bound` | Accepted focal scale sits on its search bound; widen the search or recalibrate |
| `centroid-covariance-missing` | A fitting star has no centroid covariance; use a covariance-producing measurer |
| `held-out-covariance-missing` | A verification star has no centroid covariance, so the held-out test cannot run |
| `insufficient-fitting-stars` | Fewer than `MinimumFittingStars` (12) fitting stars |
| `insufficient-held-out-stars` | Fewer than `MinimumHeldOutStars` (4) verification stars; solve with a larger verification subset |
| `derivative-unsupported` | A star or the focal scale leaves the projection domain under perturbation |
| `rank-deficient` | Stars do not constrain every pose parameter; widen the field |
| `not-converged` | Refit did not converge within `MaximumIterations` |
| `ill-conditioned` | Scaled condition number above the limit |
| `model-invalid-residual-excess` | Fitting χ² above its α quantile; declare a systematic budget or correct the model |
| `model-invalid-held-out-prediction` | Held-out χ² above its α quantile; the model does not predict unseen stars |
| `held-out-derivative-unsupported` | A verification star leaves the projection domain at the refitted pose |
| `held-out-prediction-singular` | The held-out prediction covariance is not positive definite |

Both model-validity tests are required. A frame whose held-out test cannot run is withheld, never published as
validated by the residual test alone.

The shared-calibration component can be withheld on its own as `shared-calibration-covariance-not-supplied` or
`calibration-derivative-unsupported`. The total then carries the same reason.

## Identities and conventions

`AstrometricFrameUncertainty.IdentitySha256` binds all of the following:

- the assessment, calibration, catalog, catalog selection and solver-settings identities
- the supplied readout declaration (`ReadoutIdentitySha256`, null when none is supplied), including the binning
  operation and CFA origin that leave the projection unchanged. It must be internally consistent and produce the frame
  calibration's image size.
- a measurement-input identity over the ordered detections and covariances
- the options identity
- the shared-calibration identity
- the clock facts
- `AstrometricConventionIdentity`: the UTC exposure start, end and midpoint; time scale `utc`; catalog epoch `J2000.0`;
  coordinate model; proper motion `none-fixed-position-catalog-baseline`; pose frame
  `topocentric-horizontal-enu-at-exposure-midpoint`; refraction model; pixel-coordinate convention; and the pose
  parameterization

The estimator rejects incompatible reuse rather than propagating it:
- an assessment whose calibration, catalog selection or solver settings differ from those supplied
- solver, coordinate, refraction or pixel conventions other than the ones it models
- associations that do not match the assessment's association identity
- a shared calibration whose readout view is not exactly the frame's calibration
- a readout declaration that is inconsistent or does not produce the frame calibration's image size

## Clock policy

A clock error turns the true sky about the celestial pole. It therefore moves the horizontal pose, but not the
pixel-to-equatorial mapping fitted at the stated time. No residual fit can separate it from pointing.

- The clock component is `bounded` only for a synchronized kernel that reports a maximum error. Its value is that
  error times the sidereal rate, in degrees of horizontal rotation, and `AffectsEquatorialMapping` is false.
- `NotSupplied`, `Unknown`, `Unsynchronized` and a missing maximum error each withhold the clock component with
  their own reason. None of them withholds the pose uncertainty.
- The clock bound is never added to the total.

## Evidence

**Unit** (Astronomy 23 + 7 + 1, CameraAgent 3)

- `AstrometricUncertaintyCoverageTests` (23) checks Monte Carlo coverage of the core against predeclared tolerances.
  - **Tolerances.** Coverage fractions at 0.6827/0.95/0.99 must lie within 3.5 binomial σ. The thresholds are the
    ellipsoid χ²₄ values 4.7198/9.4877/13.2767 and the per-parameter intervals 1/1.96/2.576 σ.
  - **Ideal model** (400 trials).
  - **Every lens family** (300 trials each): equidistant, equisolid, orthographic and stereographic fisheye, and a 90°
    rectilinear (perspective) lens. Each family's truth and fit use its own projection.
  - **Lens-family limit.** An orthographic lens whose circle equals its focal length is withheld as
    `derivative-unsupported` rather than throwing.
  - **Held-out test unavailable.** A verification star with no rectilinear image, or too few verification stars, fails
    the core instead of passing it unchecked.
  - **Declared systematic floor.** It is propagated and covers.
  - **Mismatched models** with a declared budget of the same size, 200 trials each. The 99% false-acceptance rate
    stays within 1% + 3.5σ:
    - omitted radial k1 of 1e-4 to 3e-3, declared as a calibration k1 prior
    - a magnitude-dependent bright-star bias of 0.02 to 0.5 px, declared as a common-mode offset
  - **Undeclared calibration error.** An undeclared k1 of 1e-3 is absorbed by the focal scale. Residuals stay consistent
    while the conditional ellipsoid misses the truth in at least half the accepted trials. This is why the total
    requires the shared calibration covariance.
  - **Undeclared gross mismatch.** k1 3e-3 or a 0.5 px bias is withheld as model-invalid in at least 99% of trials.
  - **Shared calibration** (400 paired trials). The total covers on both frames. The observed cross-frame correlation
    of every pose-parameter pair matches `S_a P S_bᵀ` within 3.5 Fisher-z σ, with a predicted correlation above 0.5.
  - Determinism, rotation-vector inversion and quantile reference values.
- `AstrometricUncertaintyEstimatorTests` (7) cover:
  - separate components bound to every identity
  - the shared-calibration total and cross-covariance
  - incompatible-reuse rejection
  - every unsupported case withholding all components
  - an optimistic covariance (10× too small) withheld as `model-invalid-residual-excess`, then accepted with a declared
    floor of the actual size
  - clock facts that bound the horizontal pose only
  - a held-out test that cannot run (missing verification covariance, too few verification stars) withholding
  - readout declarations bound into the identity even when the geometry is unchanged, and inconsistent ones rejected
- `OpticalCalibrationSessionTests` (+1) checks that the published shared covariance reproduces the reported standard
  errors and is absent on a rejected result.
- `AstrometricClockFactsMapperTests` (3) cover synchronized bounds, states that carry no bound, and the rule that an
  SNTP offset is never a bound.

**Manual: `VirtualAstrometricUncertaintyQualificationTests`** (HYG, every VirtualSky astrometry-qualified profile)

Each frame is rendered by VirtualSky, measured by the #1103 v2 measurer, solved blind and then warm, and estimated.
Only final pixels, the measured centroid covariance and the nominal (or session-calibrated) rig reach the estimator.
Truth is the rendering rig, carried to the exposure midpoint and compared through the independent reference equations:

- The pose error is the small-angle rotation vector between reference bases, never the production rotation code.
- The Mahalanobis distance uses an independent Gauss-Jordan solve.

The profiles are mono native, ROI, 2×2, ROI 2×2, mirrored, rolled and CFA. Every one counts toward acceptance, and a
withheld frame counts as a failure, never an exclusion. Each partition also runs a full V4–V5–V6 session chain:

1. A #1104 calibration on truth optics that differ from nominal in focal length (±0.3%), principal point and k1,
   fitted from four frames.
2. Three withheld frames (native, ROI and ROI 2×2), solved under the calibrated rig.
3. A total for each withheld frame, whose truth must lie inside χ²₄ at 0.999 (18.4668). The first pair's
   cross-covariance is recorded.

### Tuning partition (Feb 10, seed 110520, 8 realizations; not final evidence)

**At a zero systematic budget**, every one of the 56 frames and all 3 session frames were withheld as
`model-invalid-residual-excess`:

| Profile | Fitting χ²/dof |
| --- | --- |
| mono | ≈ 7–9 |
| mono 2×2 | ≈ 9–19 |
| CFA | ≈ 220–310 |

#1104's independent reference shows that the projection maps truth to about 0.01 px, so the excess is in the
centroids. This is the first coverage test of the #1103 covariance, which #1103 documents as
`measurement-covariance-only-unvalidated`: it omits pixel-phase sampling and interpolation error. Bisecting each
frame for the per-star floor that brings χ²/dof to one gives a stable additive floor:

| Format | Required floor, min–max (px) | Median (px) | Declared (px) |
| --- | --- | --- | --- |
| mono, unbinned | 0.037–0.045 | 0.0415 | 0.042 |
| mono, 2×2 | 0.024–0.031 | 0.0277 | 0.028 |
| CFA | 0.062–0.065 | 0.0634 | 0.063 |

The declared floor is the tuning median. A +15% margin above the tuning maximum was tried and rejected: it gave
χ²/dof 0.76 and 95%/99% ellipsoid coverage of 1.0, and the two-sided coverage test fails over-coverage as surely as
under-coverage. The budget was posted on #1105 before any held-out run.

**With the declared budget:**
- 0/56 frames withheld.
- Pooled fitting χ²/dof 0.996, and held-out 0.966.
- Pooled ellipsoid coverage 0.696/0.929/1.000 against 0.683/0.95/0.99.
- Session total Mahalanobis 1.60/1.24/1.13. Conditional-only distances were 10.9/3.96/1.22, so the shared-calibration
  component is what covers the calibrated lens's own error.

### Held-out partition

- **Run.** Jan 15/May 15/Sep 15 with seeds 110501/110505/110509, 20 realizations × 7 profiles each (420 frames), plus
  three session chains.
  - Release build, `DOTNET_TieredCompilation=0`, .NET 10.0.12, 12 processors.
  - Revision `e280ac72`, with the declared budget unchanged from tuning.
  - Report `virtual-astrometric-uncertainty.json` (schema `virtual-astrometric-uncertainty-v1`), SHA-256
    `63deb8666dea1f12b27a0e26bc34e07bfcf81a4ee27ba96d51ae1827c057de01`.
- **Result.** Passed with no failures. All 420 frames were available, with none withheld in any profile. Every repeat
  estimate had an identical identity.
- **Model validity.** Pooled fitting χ²/dof was 0.972, and held-out 0.977. The per-profile ranges were 0.954–0.990 and
  0.952–0.989.
- **Coverage** at nominal 0.6827/0.95/0.99, against bounds of 3.5 binomial σ:

| Profile (n) | Ellipsoid | Worst parameter interval | Bound |
| --- | --- | --- | --- |
| mono-native (60) | 0.767 / 0.967 / 1.000 | 0.633 / 0.967 / 0.967 | 0.210 / 0.098 / 0.045 |
| mono-roi (60) | 0.750 / 0.933 / 0.983 | 0.633 / 0.917 / 0.983 | same |
| mono-bin2 (60) | 0.717 / 0.883 / 0.967 | 0.683 / 0.933 / 0.967 | same |
| mono-roi-bin2 (60) | 0.667 / 0.917 / 0.950 | 0.567 / 0.917 / 0.967 | same |
| mono-mirror (60) | 0.700 / 0.950 / 1.000 | 0.650 / 0.917 / 0.967 | same |
| mono-roll (60) | 0.667 / 0.983 / 1.000 | 0.667 / 0.933 / 0.983 | same |
| cfa-native (60) | 0.683 / 0.967 / 1.000 | 0.600 / 0.950 / 0.983 | same |
| **pooled (420)** | **0.707 / 0.943 / 0.986** | **0.662 / 0.948 / 0.990** | **0.079 / 0.037 / 0.017** |

- **Required floor, as a held-out diagnostic only.**
  - mono 0.0363–0.0458 px, median 0.0407
  - mono 2×2 0.0236–0.0332 px, median 0.0273
  - CFA 0.0583–0.0668 px, median 0.0627

  These agree with the tuning values, so the declared floor transfers to unseen dates, pointings and noise.
- **Session chains.** The total Mahalanobis distances, against a limit of 18.4668, were:

  | Session | native | ROI | ROI 2×2 |
  | --- | --- | --- | --- |
  | Jan | 3.65 | 4.19 | 0.94 |
  | May | 8.05 | 4.78 | 2.61 |
  | Sep | 8.76 | 6.99 | 10.18 |

  Without the shared-calibration component the same errors give 22.3/15.0/1.5, 24.8/24.5/4.9 and 42.8/39.3/16.1.
  Five of the nine would exceed the limit. A cross-covariance was recorded for each chain's first pair.

  The nine totals average 5.6 against an expected 4. The rows within one chain share a calibration and are correlated,
  so this is not a test failure. It is consistent with the residual-scaled #1104 covariance being slightly optimistic,
  and #1106 should keep checking it.
- **Resources** (full-resolution frames):
  - The frame baseline (measure plus warm solve) has a median of 135.7 ms.
  - The candidate (estimator) has a median of 2.5 ms and a maximum of 44.7 ms. Its maximum ratio to its own frame is
    0.047, against a limit of 0.5. Allocation has a median of 0.68 MB and a maximum of 1.42 MB, against a 32 MiB limit.
  - Peak working set for the run was 1.44 GB, dominated by rendering.

## Limits

- The declared floors are qualified for the VirtualSky renderer and the #1103 v2 measurer at these profiles only.
  A physical camera needs its own budget from its own tuning data (#1107/#523).
- The #1104 shared covariance is residual-scaled. It is an engineering estimate of calibration error, and the session
  chain checks it against truth rather than assuming it.
- Coverage of the pose is per frame. Correlation between frames is modelled only through the shared calibration.
- The clock contributes a bound, not a covariance.
