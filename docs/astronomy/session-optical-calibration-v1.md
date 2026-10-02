# Session optical calibration — v1

Issue #1104 (V5 of #1098) adds a reusable optical calibration fitted jointly over several frames. Each frame
keeps its own pose, so pose is never folded into the optics. The result is one immutable native-sensor model,
and cropped or binned readouts are views derived from it. It builds on the
[shared astrometry library](shared-astrometry-v1.md) and the
[measured-star path](measured-star-diagnostics-v1.md).

**Not included:**
- writes to physical rigs
- per-frame lens models
- projection math inside hosts
- FITS/WCS export
- physical lens or sensor qualification, which stays with #1107/#523

## Ownership

- **`HVO.SkyMonitor.Astronomy`**
  - Owns the radial model: `RadialDistortion` and `ProjectionContext.RadialDistortionK1`.
  - Owns the fitter: `OpticalCalibrationSession.Fit`, with `OpticalCalibrationOptions` and
    `OpticalCalibrationResult` (`optical-calibration-result-v1`, fitter `session-optical-calibration-v1`).
  - Owns the readout and activation helpers: `RigProjectionContextFactory.CreateReadoutView` and
    `CreateCalibratedOptics`.
- **`HVO.SkyMonitor.AgentCore`**
  - `OpticsProfile.RadialDistortionK1` is the persisted declaration.
  - It is omitted from JSON when zero, so existing rigs, scenes and identities serialize byte-for-byte as before.
- **`HVO.SkyMonitor.CameraAgent.Common`**
  - `VirtualOpticalCalibrationReviewService` owns review, accept and reject for virtual profiles only.
  - Activation and persistence stay with the host's existing named-rig lifecycle.

## Radial model

- The distortion is radial about the principal point, applied to the family's normalized ideal radius:
  `r_d = r (1 + k1 r²)`.
  - For fisheye families, `r` is the family's ideal radial function of the off-axis angle.
  - For perspective, `r` is `tan θ`.
- **Every family supports it**: equidistant, equisolid, orthographic and perspective. The VirtualSky renderer
  projects through the same `ProjectionContext`, so emulator support follows from the rig declaration.
- **Invertibility is a validation rule, not a hope.**
  - `|k1| ≤ 0.5`.
  - The radial derivative `1 + 3 k1 r²` must stay at or above 0.5 across the whole declared aperture.
  - A circular image circle or rectangular sensor corner that reaches beyond that domain is rejected with
    `ArgumentOutOfRangeException`. It is never clamped.
  - `Undistort` inverts by bracketed Newton iteration within the supported domain. The round trip is exact to
    1e-12, and it returns NaN outside the domain.
- **Readout views.** `CreateReadoutView(native, readout)` maps the principal point, focal lengths and aperture
  through ROI and binning, and carries k1 unchanged because k1 is in normalized units. It rejects a readout whose
  native geometry differs from the model, that falls outside the sensor, that is not divisible by its binning,
  or that is empty.

## Fit

**Parameters.** Every member of `OpticalCalibrationOptions` is part of the options identity.

**Input identity.** `InputIdentitySha256` hashes, in order, every offered fit frame and every withheld frame:
its full frame context (source, descriptor, payload, observer, exposure and detection settings), readout view,
readout identity and detections. It holds even for frames an early rejection never reached. It is part of the
result identity, so different inputs never produce one calibration version.

| Parameter | Fitted | Default bound |
| --- | --- | --- |
| Focal scale (one shared scale on fx and fy; log-parameterized) | Always | ±5% of nominal |
| Principal point x, y (native photosites) | Always | ±40 px of nominal |
| Radial k1 | Only when `FitRadialDistortion` | ±0.02 |

Family, parity, aperture, image-circle radius and orientation are never fitted. The nominal value is kept for
each.

**Stages.**
1. **Acquisition.** Each fit frame is blind-solved with the ordinary bounded acquisition at nominal optics, in
   its own readout view. A frame whose best competing candidate differs by more than 2° yet matches 80% as many
   stars is `ambiguous`. No frame may fail or be ambiguous.
2. **Joint fit.** Damped Gauss–Newton runs over the shared optics plus one rotation per frame.
   - Shared-optics derivatives are finite differences. A backward step is used where the forward step would
     leave the supported domain or the bound.
   - Associations use the solver's nearest-with-1.5×-margin, one-to-one rule. The radius shrinks from 7.5 to 1.5
     px in stages, with Huber weighting.
   - Convergence is declared only at the final radius.
3. **Evaluation.** The gates are listed below.
4. **Validation.** Each withheld frame is solved independently by the ordinary single-frame solver through the
   candidate's derived readout view. Every frame must yield a measured mapping with focal scale within 0.003 of
   1.

**Gates.** Every failure is a reason-coded rejection, and only an `Accepted` result carries `CalibratedNative`.

| Gate | Default | Rejection code |
| --- | --- | --- |
| Fit frames | 3–24 | `insufficient-frames` |
| Withheld frames | ≥ 1 | `insufficient-validation-frames` |
| Sky rotation across fit frames (circular LMST span) | ≥ 15° | `insufficient-sky-rotation` |
| Per-frame fitting stars | ≥ 12 | `insufficient-associations`, `frame-support:<capture>` |
| Radial support: bins of the native aperture radius with ≥ 6 stars | ≥ 4 of 5 | `insufficient-radial-support` |
| Azimuthal support | ≥ 6 of 8 | `insufficient-azimuth-support` |
| Verification stars (held-out catalog subset, isolated) | ≥ 12 | `insufficient-verification-stars` |
| Fitting / verification RMS | ≤ 0.4 / 0.5 readout px | `fitting-residual`, `verification-residual` |
| Parameter at its declared bound | none | `parameter-bound:<name>` |
| Correlation-matrix condition number of the marginal shared-optics covariance | ≤ 1000 | `ill-conditioned` |
| A fitted parameter with no supported finite-difference direction (the fit sits on the distortion-domain boundary) | none | `derivative-unsupported`; no precision is reported |
| Marginal standard error: focal scale, principal point, k1 | ≤ 0.002, 1.5 px, 0.002 | `underconstrained:<name>` |
| Signed radial bias per supported radius bin | not both > 0.2 px and > 3σ | `radial-bias:<bin>` |
| Convergence | within 40 iterations | `not-converged` |
| Cooperative budget | 120 s | `time-budget` (`BudgetExceeded`) |
| Catalog completeness or coordinate model | declared | `catalog-incomplete`, `coordinate-model-unsupported` (`Unavailable`) |

**What the gates mean.**
- The marginal covariance is the Schur complement of the shared optics after every per-frame pose is
  eliminated. It is scaled by the unweighted residual variance. It is a conditional least-squares uncertainty,
  not a calibrated probability.
- The radial-bias gate is how an omitted distortion term or a wrong projection family is detected: either one
  leaves a systematic signed radial residual that pose and focal scale cannot absorb.
- A failed session proposes no optics, so the previously accepted calibration stays authoritative.

## Host review and activation (virtual profiles only)

`VirtualOpticalCalibrationReviewService` reviews an `OpticalCalibrationResult` against the active named rig.

**Accept is blocked when:**
- the rig is physical (any module other than `VirtualSky`)
- a rig change is already pending
- the result is not accepted
- the result's nominal native model differs from the active rig's
- the calibration cannot be represented as an `OpticsProfile`

**Representation.** `CreateCalibratedOptics` replaces only the principal point, focal lengths and k1, plus the
calibration version `session-optical-calibration-v1:<result identity>`. It throws unless the new profile
reproduces the calibrated native model exactly.

**Decisions.**
- **Accept.**
  - Re-reviews first, and refuses a stale review (changed rig or optics revision, or a changed selection version).
  - `SqliteNamedRigProfileStore.RetainCalibratedOpticsAsync` then does everything else in one SQLite transaction:
    - rechecks that the reviewed revision is still active at the reviewed selection version with nothing pending
    - looks up an existing draft with the same profile, camera, mount and optics
    - otherwise writes one immutable optics revision and composes one draft rig revision
  - A stage, activation or cancellation after review is a conflict.
  - A failure or cancellation before commit writes nothing, and concurrent accepts serialize into one draft.
  - Never stages or activates. Activation follows the existing preview, stage and restart path.
  - Accepting the same result again returns the existing revision.
- **Reject.** Writes nothing. Reject decisions are not durably journaled in v1.

Both keep the previously active revision and its optics unchanged.

## Evidence

**Unit**
- `RadialDistortionTests` (6) cover:
  - inversion
  - every family's round trip against the closed form
  - noninvertible and out-of-range rejection
  - zero-coefficient serialization identity
  - readout-view mapping
  - blind-search scales kept inside the supported domain for distorted perspective optics
- `OpticalCalibrationSessionTests` (12) cover:
  - recovery within tolerance, with pose kept per frame and out of the session optics
  - ROI/bin views derived from one native calibration
  - determinism and option identity
  - omitted distortion
  - wrong family (equisolid truth against an equidistant nominal)
  - insufficient sky rotation, frames or radial support
  - ambiguity (a rotationally symmetric sky)
  - incomplete catalog and invalid inputs
  - exhausted budget
  - an input identity bound to every fit and withheld frame input
  - no derivative direction at the distortion-domain boundary, reported rather than thrown
- `VirtualOpticalCalibrationReviewServiceTests` (6) cover:
  - accept, reject, retention and idempotency
  - stale reviews, including a stage or cancellation between review and accept
  - failure or cancellation between the optics and rig writes, and concurrent accepts
  - physical-rig and foreign-nominal blocks
  - `CreateCalibratedOptics` refusals

**HYG qualification (Manual): `VirtualOpticalCalibrationQualificationTests`**
- **Rendering.** The real `VirtualSkyCameraModule` renders the ASI174 mono profile with truth optics: perturbed
  focal scale, principal point and k1, with a truth pose.
- **Fitter inputs.** The fitter sees only final pixels, measured by `linear-stellar-local-v2`, and the
  *nominal* native model.
- **Fit frames.** native at 05:00Z, ROI at 06:30, native at 08:00, bin2 at 09:00 UTC.
- **Withheld frames.** native and ROI at 07:15, ROI+bin2 at 09:30.
- **Independent scoring.** Each withheld frame is re-measured and solved through the calibrated readout view,
  then scored by `VirtualAstrometryReference`. That reference implements the equidistant projection with k1
  independently of production projectors and must give:
  - association precision of 1
  - withheld mapping RMS ≤ 0.5 px and p95 ≤ 0.75 px
  - pose error ≤ 0.06°
  - focal error ≤ 0.001
- **Omitted-distortion control.** Each partition is refit with `FitRadialDistortion: false`. It must be rejected
  when truth k1 ≠ 0 and accepted when k1 = 0.
- **Activation.** The calibrated optics must reproduce the calibration exactly and initialize a VirtualSky module.
- **Output.** `virtual-optical-calibration.json` (`virtual-optical-calibration-v1`).

**Tolerances** were predeclared on #1104 before any held-out run:

| Gate | Limit |
| --- | --- |
| Calibration | Accepted |
| Focal-scale relative error | ≤ 0.001 |
| Principal-point error | ≤ 1 native px |
| k1 error | ≤ 0.0015 |
| Native intrinsic-map error over zenith angle ≤ 85° (reference equations) | ≤ 0.5 px |
| Withheld validation verification RMS | ≤ 0.5 readout px |
| Session fit wall time | ≤ min(60 s, 1.25 × solves × single cold solve + 2 s) |
| Session fit allocation | ≤ 1.25 × solves × single cold solve + 256 MiB |
| Process peak working set | ≤ 2 GiB |

**Resource baseline.** The baseline is one ordinary cold single-frame solve of the first native fit frame at
nominal optics, measured in the same process after the fits. `solves` is the 4 fit plus 3 withheld frames. The
session's fixed cost is one blind acquisition per fit frame and one ordinary solve per withheld frame, so the
bound says the joint fit adds only bounded overhead to that unavoidable work. The harness requires
`DOTNET_TieredCompilation=0`. Run one partition at a time:

```bash
HVO_EVIDENCE_REVISION=<committed sha> DOTNET_TieredCompilation=0 HVO_ASTROMETRY_CATALOG_ROOT=<catalog> \
  dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests -c Release \
  --filter "FullyQualifiedName~VirtualOpticalCalibrationQualificationTests.HeldOutOpticalCalibration"
```

### Tuning partition (Feb 10, seed 110420; not final evidence)

**Truth:** focal ×1.010, principal point (+5, −4) px, k1 −0.007, pose 82/137/13.

**Accuracy.** Accepted.

| Error | Value | Limit |
| --- | --- | --- |
| Focal | 2.0e-5 | 0.001 |
| Principal point | 0.004 px | 1 px |
| k1 | 1.0e-5 | 0.0015 |
| Intrinsic map | 0.008 px | 0.5 px |

**Withheld frames.** Independent RMS was 0.012 px (native), 0.010 px (ROI) and 0.005 px (ROI+bin2).

**Omitted-distortion control.** Rejected with `fitting-residual`, `verification-residual` and `radial-bias` in
all five radius bins.

**Cost.**

| Run | Wall time | Allocated | Peak working set |
| --- | --- | --- | --- |
| Cold session | 34.1 s (acquisition 19.0 s, joint fit 0.24 s, validation 14.9 s) | 17.6 GiB | 456 MiB |
| Single cold solve (baseline) | 5.8 s | 3.4 GiB | — |

- The joint fit is under 1% of the session.
- The session costs less than its seven constituent solves.
- The solver's 3.4 GiB per cold solve predates this change. It is recorded for #1106's resource gate.

No setting was changed after tuning.

### Held-out partition

**Run.** Revision `21f6015570086a6b679c1a31d9e5012ab97f0c69`, Release, .NET 10.0.12, tiered compilation off, 12 processors. Single process, 1 m 55 s.

This rerun follows the review corrections. Against the first run at `4b0305f3`, every fitted parameter,
error, score, rejection and diagnostic is identical. Only timing noise and the result identities changed; the
identities now include the input identity.

**Report.** `virtual-optical-calibration.json` (schema `virtual-optical-calibration-v1`), SHA-256 `5332b1cf570409fc2f2090f350d653f65e33c18130dd8b10d558abaac97a879e`.

All three cases were Accepted and every predeclared tolerance held. No setting was changed after the run.

**Truth.**

| Case | Date | Seed | Focal | Principal offset | k1 | Pose |
| --- | --- | --- | --- | --- | --- | --- |
| 01 | Jan 15 | 110401 | ×1.008 | (+4.5, −3.5) px | −0.008 | 78/205/−21 |
| 05 | May 15 | 110405 | ×0.992 | (−6, +2.5) px | +0.006 | 85/40/33 |
| 09 | Sep 15 | 110409 | ×1.004 | (+3, +5) px | 0 | 80/300/5 |

**Accuracy against the reference equations.**

| Case | Focal (≤ 0.001) | Principal point (≤ 1 px) | k1 (≤ 0.0015) | Intrinsic map (≤ 0.5 px) |
| --- | --- | --- | --- | --- |
| 01 | 1.3e-5 | 0.004 px | 1.3e-6 | 0.010 px |
| 05 | 2.6e-9 | 0.005 px | 3.2e-6 | 0.009 px |
| 09 | 3.0e-5 | 0.008 px | 1.8e-5 | 0.014 px |

**Fit diagnostics.**
- Every case: sky rotation 60.2°, four fitted frames, 5/5 radial bins and 8/8 azimuth bins occupied, converged.
- Fitting RMS was 0.107–0.130 px and verification RMS 0.076–0.083 px.
- The marginal condition number was 25.8–34.6.
- No parameter hit a bound.

**Validation through the calibrated readout views.** The fitter's own validations were Accepted with verification RMS 0.049–0.087 px (limit 0.5 px).

**Withheld frames, scored independently.** Precision was 1 with no missing mappings throughout.

| Case | Native | ROI | ROI+bin2 |
| --- | --- | --- | --- |
| 01 | 0.007 px (p95 0.010) | 0.003 px (p95 0.006) | 0.007 px (p95 0.010) |
| 05 | 0.009 px (p95 0.016) | 0.009 px (p95 0.011) | 0.010 px (p95 0.012) |
| 09 | 0.006 px (p95 0.010) | 0.009 px (p95 0.013) | 0.013 px (p95 0.019) |

The maximum pose error was 0.004° and the maximum focal error 4.8e-5.

**Omitted-distortion control.**
- Case 01 (k1 −0.008): Rejected with `fitting-residual`, `verification-residual` and `radial-bias` in bins 0–3.
- Case 05 (k1 +0.006): Rejected with `fitting-residual`, `verification-residual` and `radial-bias` in bins 0–4.
- Case 09 (k1 = 0): Accepted, as declared.

**Cost.** Each limit is derived from that case's own single-frame cold-solve baseline.

| Case | Kind | Wall time (limit) | Allocated (limit) | Peak working set (≤ 2 GiB) | Baseline solve |
| --- | --- | --- | --- | --- | --- |
| 01 | cold | 19.0 s (25.4 s) | 18.5 GiB (23.8 GiB) | 477 MiB | 2.7 s, 2.7 GiB |
| 05 | session | 14.5 s (18.0 s) | 14.8 GiB (17.6 GiB) | 477 MiB | 1.8 s, 2.0 GiB |
| 09 | session | 14.9 s (19.7 s) | 15.4 GiB (19.1 GiB) | 477 MiB | 2.0 s, 2.1 GiB |

Measuring each frame took 33–189 ms, with 653–719 candidates per frame.
